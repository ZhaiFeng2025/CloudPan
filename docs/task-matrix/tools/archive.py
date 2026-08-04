# -*- coding: utf-8 -*-
"""
CloudPan 任务契约聚合工具（v4）：归档 + 派生视图同步 + 渲染。

设计动机：executor/verifier 只读写 active/T-{id}.json 单卡；「done 卡归档到 history、
sync_state（state/tasks-index/meta 与卡对齐）、合并目标度量、渲染 INDEX.md」等聚合操作
由本脚本幂等完成，避免 AI 手改多文件 JSON 出错。

用法:
    python docs/task-matrix/tools/archive.py            # sync_state → check → 归档 → 渲染 INDEX + findings-index
    python docs/task-matrix/tools/archive.py --render   # 仅重渲染 INDEX.md + findings-index（不归档）
    python docs/task-matrix/tools/archive.py --check    # 只校验契约一致性，不改动
    python docs/task-matrix/tools/archive.py --goals    # sync_state → check → 合并 .reviews/goals/ + 目标修订 + 健康检测
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
CONTRACT = os.path.join(ROOT, 'docs', 'task-matrix', 'contract')
ACTIVE = os.path.join(CONTRACT, 'active')
HISTORY = os.path.join(CONTRACT, 'history')
STATE = os.path.join(CONTRACT, 'state.json')
INDEX_JSON = os.path.join(CONTRACT, 'tasks-index.json')
META = os.path.join(CONTRACT, 'meta.json')
FINDINGS = os.path.join(CONTRACT, 'findings.json')
FINDINGS_INDEX = os.path.join(CONTRACT, 'findings-index.json')
GOALS = os.path.join(CONTRACT, 'goals.json')
REVIEWS = os.path.join(ROOT, 'docs', 'task-matrix', '.reviews')
GOALS_REVIEW = os.path.join(REVIEWS, 'goals')
INDEX_MD = os.path.join(ROOT, 'docs', 'task-matrix', 'INDEX.md')

DIM_LABEL = {'architecture': '架构', 'function': '功能', 'simplicity': '技术简洁', 'ux': 'UX'}
CATEGORY_LABEL = {'function': '功能', 'performance': '性能', 'polish': '美化'}
CATEGORY_ORDER = {'function': 0, 'performance': 1, 'polish': 2, None: 3}
STATUS_LABEL = {'todo': '待办', 'in-progress': '进行中', 'acceptance': '待验收', 'done': '已完成'}
GOAL_STATUS_LABEL = {'active': '进行中', 'achieved': '已达成', 'parked': '暂停', 'archived': '已归档'}
GOAL_DIR_LABEL = {'down': '↓', 'up': '↑', 'flat': '≈'}


def _load(path):
    try:
        with open(path, encoding='utf-8') as f:
            return json.load(f)
    except (OSError, ValueError) as e:
        print(f'[ERROR] 契约文件不可解析: {path}（{e}）')
        raise SystemExit(1)


def _save(path, data):
    """原子写：先写 .tmp 再 rename，进程中断不写坏 JSON。"""
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


def _task_num(fn):
    """从文件名 T-109.json 提取数值序（非任务文件如 .gitkeep 排最后）。"""
    name = fn[:-5] if fn.endswith('.json') else fn
    if name.startswith('T-'):
        try:
            return int(name[2:])
        except ValueError:
            pass
    return 10 ** 9


def _read_cards():
    """读 active/ 下全部任务卡，返回 {id: task}。"""
    cards = {}
    if not os.path.isdir(ACTIVE):
        return cards
    for fn in sorted(os.listdir(ACTIVE), key=_task_num):
        if fn.endswith('.json'):
            p = os.path.join(ACTIVE, fn)
            cards[fn[:-5]] = _load(p)
    return cards


def archive() -> dict:
    """把 status=done 的卡归档到 history/batch-NN.json，更新 state/tasks-index，删除卡。返回归档数。"""
    cards = _read_cards()
    done_cards = {tid: t for tid, t in cards.items() if t.get('status') == 'done'}
    if not done_cards:
        return {'archived': 0}

    # 1. 按 batch 归并到 history
    by_batch: dict[int, list] = {}
    for t in done_cards.values():
        by_batch.setdefault(t['batch'], []).append(t)

    for bn, ts in by_batch.items():
        hp = os.path.join(HISTORY, f'batch-{bn:02d}.json')
        entry = _load(hp) if os.path.exists(hp) else {'batch': bn, 'tasks': []}
        existing_ids = {t['id'] for t in entry.get('tasks', [])}
        entry.setdefault('tasks', [])
        for t in ts:
            if t['id'] not in existing_ids:
                entry['tasks'].append(t)
        _save(hp, entry)

    # 2. state.json 移除
    state = _load(STATE)
    active_ids = set(done_cards)
    state['active'] = [a for a in state.get('active', []) if a['id'] not in active_ids]
    _save(STATE, state)

    # 3. tasks-index.json 更新状态
    idx = _load(INDEX_JSON)
    for item in idx.get('tasks', []):
        if item['id'] in done_cards:
            item['status'] = 'done'
    _save(INDEX_JSON, idx)

    # 4. 删除已归档卡
    for tid in done_cards:
        os.remove(os.path.join(ACTIVE, f'{tid}.json'))

    return {'archived': len(done_cards)}


def sync_state():
    """从 active/ 卡重建派生视图（state.json + tasks-index 活跃卡状态 + meta 统计）（卡为真值，幂等）。

    根治 executor/verifier 只写卡、派生视图不同步的漂移（如卡 acceptance 而 state/index 仍 todo）——
    领取列表、detect_goal_health、收敛判定都依赖这些派生视图，不同步会导致依赖链不解锁、可领取空假真、健康检测失明。
    在 all 模式 check/archive 前、--goals 模式 check 前、/mission 每波领取前调用。
    """
    cards = _read_cards()
    rows = []
    for tid in sorted(cards, key=lambda x: int(x[2:])):
        t = cards[tid]
        rows.append({
            'id': t['id'],
            'title': t['title'],
            'dimension': t['dimension'],
            'priority': t['priority'],
            'status': t['status'],
            'batch': t['batch'],
            'dependsOn': t.get('dependsOn', []),
            'attempts': t.get('attempts', 0),
        })
    _save(STATE, {'active': rows})

    # 对齐 tasks-index 活跃卡状态（卡为真值）
    idx = _load(INDEX_JSON)
    idx_by_id = {t['id']: t for t in idx.get('tasks', [])}
    for tid, t in cards.items():
        if tid in idx_by_id and idx_by_id[tid].get('status') != t.get('status'):
            idx_by_id[tid]['status'] = t['status']
    _save(INDEX_JSON, idx)

    # 刷新 meta 统计（归档/产出后不再漂移）
    meta = _load(META)
    meta['totalTasks'] = len(idx.get('tasks', []))
    meta['activeTasks'] = len(cards)
    _save(META, meta)
    return len(rows)


def render_findings_index():
    """从 findings.json 重建 findings-index.json（摘要行，供 producer 去重/编号，不读全量 44k）。

    findings.json 保留完整 problem/why 追溯；findings-index 只存摘要（幂等，AI 不手写）。
    """
    if not os.path.exists(FINDINGS):
        return 0
    findings = _load(FINDINGS)
    rows = []
    for f in findings:
        problem = f.get('problem', '')
        title = problem.split('\n')[0][:60] if problem else ''
        rows.append({
            'id': f.get('id'),
            'dimension': f.get('dimension'),
            'severity': f.get('severity'),
            'title': title,
            'location': f.get('location'),
        })
    _save(FINDINGS_INDEX, {'findings': rows})
    return len(rows)


def render_index():
    """从 tasks-index.json + meta.json + goals.json 渲染 INDEX.md（活状态板 + 目标面板）。"""
    idx = _load(INDEX_JSON)
    meta = _load(META)
    tasks = idx.get('tasks', [])

    n_done = sum(1 for t in tasks if t['status'] == 'done')
    n_acc = sum(1 for t in tasks if t['status'] == 'acceptance')
    n_act = sum(1 for t in tasks if t['status'] in ('in-progress', 'todo'))

    lines = [
        '# CloudPan 任务矩阵 — 状态板',
        '',
        f"> 契约: docs/task-matrix/contract/（schemaVersion={meta.get('schemaVersion', 3)}）｜ 更新: {meta.get('generatedAt', '')}",
        f"> 统计: {len(tasks)} 任务 = {n_done} done / {n_acc} 待验收 / {n_act} 待办",
        '',
    ]
    # 目标面板（v4；goals.json 缺失则跳过，向后兼容 v3）
    if os.path.exists(GOALS):
        goals = _load(GOALS).get('goals', [])
        lines += _render_goal_panel(tasks, goals)
    lines += [
        '| ID | 标题 | 维度 | 优先级 | 状态 | 批次 |',
        '|---|---|---|---|---|---|',
    ]
    for t in sorted(tasks, key=lambda x: int(x['id'][2:])):
        dim = DIM_LABEL.get(t['dimension'], t['dimension'])
        st = STATUS_LABEL.get(t['status'], t['status'])
        lines.append(f"| {t['id']} | {t['title']} | {dim} | {t['priority']} | {st} | {t['batch']} |")

    with open(INDEX_MD, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines) + '\n')
    return len(tasks)


def _render_goal_panel(tasks, goals):
    """目标面板：从 goals.json 层级渲染（vision→domain→metric），关联任务由 goalRef 反查。

    vision/domain 级为组织层，不度量（currentValue/target 可为 null）；状态列显示「子x/y」
    （递归统计 leaf 达成数），收敛只看 leaf。benchmark.reference 追加到指标列。
    """
    if not goals:
        return []
    by_goal = {}
    for t in tasks:
        gr = t.get('goalRef')
        if gr:
            by_goal.setdefault(gr, []).append(t['id'])
    gmap = {g['id']: g for g in goals}
    children = {}
    roots = []
    for g in goals:
        p = g.get('parent')
        if p and p in gmap:
            children.setdefault(p, []).append(g['id'])
        else:
            roots.append(g['id'])
    roots.sort()
    for cid in children:
        children[cid].sort()

    lines = [
        '## 目标面板',
        '',
        '| ID | 分类 | 指标 | 基线 | 当前值 | 目标 | 方向 | 状态 | 依据 kb | 关联任务 |',
        '|---|---|---|---|---|---|---|---|---|---|',
    ]

    def leaf_count(gid):
        g = gmap[gid]
        kids = children.get(gid, [])
        if not kids:
            return (1 if g.get('status') == 'achieved' else 0), 1
        a = t = 0
        for k in kids:
            ka, kt = leaf_count(k)
            a += ka
            t += kt
        return a, t

    def render_tree(gid, depth):
        g = gmap[gid]
        kids = children.get(gid, [])
        prefix = '　' * depth
        cat = CATEGORY_LABEL.get(g.get('category'), '—') if g.get('category') else '—'
        m = g.get('metric') or {}
        metric_str = f"{m.get('name', '')}({m.get('unit', '')})" if m.get('name') else g.get('title', '')
        bm = g.get('benchmark')
        if bm and bm.get('reference'):
            metric_str += f"·对标{bm['reference']}"
        base = g.get('baseline') if g.get('baseline') is not None else '—'
        cur = g.get('currentValue') if g.get('currentValue') is not None else '—'
        tgt = g.get('target') if g.get('target') is not None else '—'
        dir_ = GOAL_DIR_LABEL.get(g.get('direction'), g.get('direction')) if g.get('direction') else '—'
        st = GOAL_STATUS_LABEL.get(g.get('status'), g.get('status')) if g.get('status') else '—'
        if kids:
            a, t = leaf_count(gid)
            st = f"子{a}/{t}"
        kb = g.get('kbRef')
        if kb and isinstance(kb, dict) and kb.get('file'):
            kb_str = os.path.basename(kb['file']).replace('.md', '') + ' ' + (kb.get('section') or '')
        else:
            kb_str = '—'
        rel = ','.join(by_goal.get(g['id'], [])) or '—'
        lines.append(f"| {prefix}{g['id']} | {cat} | {metric_str} | {base} | {cur} | {tgt} | {dir_} | {st} | {kb_str} | {rel} |")
        for k in sorted(kids, key=lambda x: CATEGORY_ORDER.get(gmap[x].get('category'), 3)):
            render_tree(k, depth + 1)

    # 根排序：组织层（vision/domain）优先，metric 根按 category（功能→性能→美化）
    for r in sorted(roots, key=lambda x: (0 if gmap[x].get('level') in ('vision', 'domain') else 1,
                                          CATEGORY_ORDER.get(gmap[x].get('category'), 3))):
        render_tree(r, 0)
    lines.append('')
    return lines


def _kb_section_exists(full_path, section_num):
    """校验 kb 文件含 `## N. 标题` 章节（精确编号，防 `## 6.1` 误匹配 `## 6`）。"""
    try:
        with open(full_path, encoding='utf-8') as f:
            for line in f:
                if line.startswith(f'## {section_num}. ') or line.rstrip() == f'## {section_num}.':
                    return True
    except OSError:
        return False
    return False


def _sample_met(val, tgt, direction):
    """单个样本是否达标（数值强转防类型混用：字符串 "9" 与 "10" 字典序比较错误）。"""
    if val is None or tgt is None:
        return False
    try:
        val = float(val)
        tgt = float(tgt)
    except (TypeError, ValueError):
        return False
    d = direction or 'flat'
    if d == 'down':
        return val <= tgt
    if d == 'up':
        return val >= tgt
    return abs(val - tgt) <= max(1, abs(tgt) * 0.01)


def _met_target(g):
    """按 direction 判定 currentValue 是否达到 target（数值强转）。"""
    return _sample_met(g.get('currentValue'), g.get('target'), g.get('direction'))


def merge_goals() -> dict:
    """合并 .reviews/goals/*.json 度量结果到 contract/goals.json（v4）。

    聚合操作按仓库约定由脚本幂等执行：4 个维度审查子 Agent 并发写 .reviews/goals/，
    指挥层在此统一合并回契约，避免并发写同一契约文件。达标（command 类）自动置 achieved；
    assess 类达标仅标注待人工确认，不自封达成。
    """
    if not os.path.exists(GOALS):
        print('ERROR: contract/goals.json 不存在（先运行 migrate-v3-to-v4.py）')
        return {'merged': 0}
    goals_data = _load(GOALS)
    goals = {g['id']: g for g in goals_data.get('goals', [])}

    reviews = []
    if os.path.isdir(GOALS_REVIEW):
        for fn in sorted(os.listdir(GOALS_REVIEW)):
            if fn.endswith('.json'):
                reviews.append(_load(os.path.join(GOALS_REVIEW, fn)))

    batch = _load(META).get('currentBatch')
    merged = 0
    for r in reviews:
        for item in r.get('goals', []):
            g = goals.get(item['id'])
            if not g:
                print(f'[WARN] 度量结果含未知 goal id: {item.get("id")}（已跳过，不静默丢弃）')
                continue
            if not item.get('measured'):
                continue
            if g.get('level') == 'vision':
                continue  # 愿景级不度量不判定（无 currentValue），由子目标派生
            g['currentValue'] = item['currentValue']
            if item.get('lastMeasuredAt'):
                g['lastMeasuredAt'] = item['lastMeasuredAt']
            if item.get('measureNote'):
                g['measureNote'] = item['measureNote']
            # progress 轨迹（幂等：同 batch 同 currentValue 不重复追加）
            prog = g.setdefault('progress', [])
            if not (prog and prog[-1].get('batch') == batch
                    and prog[-1].get('currentValue') == item['currentValue']):
                prog.append({'batch': batch, 'currentValue': item['currentValue'],
                             'measuredAt': item.get('lastMeasuredAt')})
            # 达标判定：连续 2 轮达标才置 achieved（防单样本噪声）；已 achieved 但回落 → 回退 active
            cur_ok = _sample_met(g.get('currentValue'), g.get('target'), g.get('direction'))
            prog = g.get('progress') or []
            prev_ok = len(prog) >= 2 and _sample_met(prog[-2].get('currentValue'), g.get('target'), g.get('direction'))
            if g['status'] == 'achieved' and not cur_ok:
                g['status'] = 'active'  # 度量回落 → 回退
            if g['status'] == 'active' and cur_ok and prev_ok:
                if (g.get('measure') or {}).get('type') == 'command':
                    g['status'] = 'achieved'
                else:
                    note = g.get('measureNote') or ''
                    if '已达标，待人工确认' not in note:  # 幂等：不重复累积标记
                        g['measureNote'] = (note + ' | ' if note else '') + '已达标，待人工确认'
            merged += 1
    applied = apply_target_revisions(goals_data)  # 消费目标修订建议（自动修订闭环）
    _save(GOALS, goals_data)
    report = detect_goal_health()
    n = render_index()
    print(f'OK: merged {merged} goal measurements, INDEX.md rendered ({n} tasks)'
          f' | health: stalled={len(report["stalled"])} jittery={len(report["jittery"])}'
          f' staleCriterion={len(report["staleCriterion"])} | target-revisions applied={applied}')
    return {'merged': merged, 'health': report, 'revisions': applied}


def detect_goal_health():
    """基于 progress 轨迹 + tasks-index goalRef，检测目标健康并写 .run/goal-health.json。

    三维度（自动优化机制的数据源）：
    - 停滞（效率/正确性）：连续 2 轮 currentValue 无变化 且 该目标无未闭合差距任务
    - 抖动（可靠性）：最后 3 轮 currentValue 交替增减（度量不稳定、不可复现）
    - 判据失效（正确性）：目标有差距任务且全 done，但 currentValue 未达 target
    """
    if not os.path.exists(GOALS):
        return {'stalled': [], 'jittery': [], 'staleCriterion': []}
    gd = _load(GOALS)
    idx = _load(INDEX_JSON)
    # 任务状态真值表：active 卡覆盖 tasks-index（卡为真值，防派生视图陈旧失明）
    task_status = {t['id']: t.get('status') for t in idx.get('tasks', [])}
    for tid, t in _read_cards().items():
        task_status[tid] = t.get('status')
    tasks_by_goal = {}
    for t in idx.get('tasks', []):
        gr = t.get('goalRef')
        if gr:
            tasks_by_goal.setdefault(gr, []).append(t)

    stalled, jittery, stale = [], [], []
    for g in gd.get('goals', []):
        gid = g.get('id')
        if not gid or g.get('status') != 'active':
            continue
        cur = g.get('currentValue')
        tgt = g.get('target')
        prog = g.get('progress') or []
        tasks = tasks_by_goal.get(gid, [])
        open_tasks = [t for t in tasks if task_status.get(t['id']) in ('todo', 'in-progress', 'acceptance')]

        if cur is not None and tgt is not None and not _met_target(g):
            # 判据失效：有差距任务且全 done 未达，或 0 差距任务未达（判据不可操作/不可达）
            if tasks and not open_tasks:
                stale.append(gid)
            elif not tasks:
                stale.append(gid)
        if len(prog) >= 2 and cur is not None and not open_tasks \
                and prog[-2].get('currentValue') == prog[-1].get('currentValue'):
            stalled.append(gid)
        # 抖动门：排除「有未闭合差距任务」的在建目标（功能推进中 currentValue 自然波动不误判）
        if len(prog) >= 3 and not open_tasks:
            v = []
            for p in prog[-3:]:
                try:
                    v.append(float(p.get('currentValue')))
                except (TypeError, ValueError):
                    break
            if len(v) == 3 and ((v[1] > v[0] and v[1] > v[2]) or (v[1] < v[0] and v[1] < v[2])):
                jittery.append(gid)

    run_dir = os.path.join(ROOT, 'docs', 'task-matrix', '.run')
    os.makedirs(run_dir, exist_ok=True)
    report = {'stalled': sorted(stalled), 'jittery': sorted(jittery), 'staleCriterion': sorted(stale)}
    with open(os.path.join(run_dir, 'goal-health.json'), 'w', encoding='utf-8') as f:
        json.dump(report, f, ensure_ascii=False, indent=1)
    return report


def apply_target_revisions(goals_data):
    """消费 .run/target-revisions.json（producer 产的目标修订建议），自动重设 target + note 审计。

    判据失效目标的修订建议：{ goalId, newTarget, basis, kbUpdateTaskId }。
    merge_goals 保存前调用；应用后删除建议文件（幂等消费）。target 修订记录在 goal.note 可审计。
    """
    rev_path = os.path.join(ROOT, 'docs', 'task-matrix', '.run', 'target-revisions.json')
    if not os.path.exists(rev_path):
        return 0
    with open(rev_path, encoding='utf-8') as f:
        revisions = json.load(f)
    goals_by_id = {g['id']: g for g in goals_data.get('goals', [])}
    applied = 0
    for rev in revisions:
        gid = rev.get('goalId')
        g = goals_by_id.get(gid)
        if not g or rev.get('newTarget') is None:
            continue
        old = g.get('target')
        g['target'] = rev['newTarget']
        audit = f"【自动修订】target 原 {old} → 新 {rev['newTarget']}，依据：{rev.get('basis', '')}"
        note = g.get('note') or ''
        g['note'] = (note + '\n' + audit) if note else audit
        applied += 1
    os.remove(rev_path)
    return applied


def check():
    """一致性校验：active 卡 status 非 done、state/tasks-index/active 三方一致、id 唯一、goals 合法。"""
    errors = []
    _check_goals(errors)
    cards = _read_cards()
    ids = list(cards)
    if len(ids) != len(set(ids)):
        errors.append('active 卡 id 不唯一')

    for tid, t in cards.items():
        if t['id'] != tid:
            errors.append(f'文件 {tid}.json 与卡内 id={t["id"]} 不一致')
        # 注：active 卡为 done 是合法中间态（verifier 置 done 后由 /mission 在批次收尾统一归档），
        # 不作为一致性错误——check 只校验结构，不耦合归档时序。

    state = _load(STATE)
    state_ids = {a['id'] for a in state.get('active', [])}
    if state_ids != set(ids):
        errors.append(f'state.json 与 active/ 不一致（state={len(state_ids)} active={len(ids)}）')
    else:
        # 状态一致性 warning（不阻塞；卡为真值，state 是派生视图，运行 archive.py 即自动同步）
        state_by_id = {a['id']: a for a in state.get('active', [])}
        mism = [f'{tid}: state={state_by_id[tid].get("status")}/卡={cards[tid].get("status")}'
                for tid in ids if state_by_id[tid].get('status') != cards[tid].get('status')]
        if mism:
            print(f'[WARN] {len(mism)} 个卡状态与 state.json 不一致（卡为真值，将自动同步）：' + '; '.join(mism[:5]) + (' …' if len(mism) > 5 else ''))

    idx = _load(INDEX_JSON)
    idx_ids = {t['id'] for t in idx.get('tasks', [])}
    all_ids = set(ids)
    # 加载 history 全部 id
    hist_ids = set()
    if os.path.isdir(HISTORY):
        for fn in os.listdir(HISTORY):
            if fn.endswith('.json'):
                hist_ids.update(t['id'] for t in _load(os.path.join(HISTORY, fn)).get('tasks', []))
    if all_ids & hist_ids:
        errors.append(f'任务同时存在于 active/ 与 history/: {sorted(all_ids & hist_ids)}')
    if not idx_ids.issuperset(all_ids | hist_ids):
        errors.append('tasks-index.json 缺少部分任务')
    # findingId 可解析（spec §4.9）：active 卡 findingId 对照 findings-index
    if os.path.exists(FINDINGS_INDEX):
        fid = {f.get('id') for f in _load(FINDINGS_INDEX).get('findings', [])}
        for tid, t in cards.items():
            f = t.get('findingId')
            if f and f not in fid:
                errors.append(f'{tid} findingId={f} 在 findings-index 中不存在')
    else:
        errors.append('findings-index.json 缺失（先跑 archive.py --render 重建）')
    return errors


def _check_goals(errors):
    """goals.json 合法性校验（v4）。goals.json 缺失时不报错（向后兼容 v3 契约）。"""
    if not os.path.exists(GOALS):
        return
    gd = _load(GOALS)
    if gd.get('schemaVersion') != 4:
        errors.append(f'goals.json schemaVersion={gd.get("schemaVersion")}，期望 4')
    meta = _load(META)
    if meta.get('schemaVersion') != 4:
        errors.append(f'meta.schemaVersion={meta.get("schemaVersion")}，期望 4（v4 含目标模块）')

    goals = gd.get('goals', [])
    ids = [g['id'] for g in goals]
    if len(ids) != len(set(ids)):
        errors.append('goals id 不唯一')
    gmap = {g.get('id'): g for g in goals}
    LEVELS = {'vision', 'domain', 'metric'}
    CATEGORIES = {'function', 'performance', 'polish'}
    # children 索引：判断 leaf（无子目标 = leaf，必须 category）
    children = {}
    for g in goals:
        p = g.get('parent')
        if p and p in gmap:
            children.setdefault(p, []).append(g['id'])
    for g in goals:
        gid = g.get('id')
        if not gid:
            errors.append('存在缺 id 的 goal')
            continue
        level = g.get('level') or 'metric'
        if level not in LEVELS:
            errors.append(f'{gid} level={level} 非法（vision|domain|metric）')
        is_org = level in ('vision', 'domain')  # 组织层：可量化也可不量化
        cat = g.get('category')
        if cat is not None and cat not in CATEGORIES:
            errors.append(f'{gid} category={cat} 非法（function|performance|polish）')
        if level == 'metric' and not cat:
            errors.append(f'{gid} metric 级目标必含 category（function|performance|polish）')
        if level == 'metric' and gid in children:
            errors.append(f'{gid} metric 级目标不能有子目标（应为 leaf）')
        if not is_org and g.get('direction') not in GOAL_DIR_LABEL:
            errors.append(f"{gid} direction={g.get('direction')} 非法（组织层可空）")
        if g.get('status') not in GOAL_STATUS_LABEL:
            errors.append(f"{gid} status={g.get('status')} 非法")
        mt = (g.get('measure') or {}).get('type')
        if not is_org and mt not in ('command', 'assess'):
            errors.append(f"{gid} measure.type={mt} 非法（command|assess，组织层可空）")
        if not is_org and g.get('target') is None:
            errors.append(f'{gid} 缺 target（组织层可空）')
        if not is_org and mt == 'assess' and not (g.get('measure') or {}).get('rubric'):
            errors.append(f'{gid} assess 目标必含 measure.rubric')
        # kbRef 校验：assess 目标必填（判据来源），command 可空
        kb = g.get('kbRef')
        if not is_org and mt == 'assess':
            if not kb or not isinstance(kb, dict):
                errors.append(f'{gid} assess 目标必含 kbRef（知识库判据来源，file+section）')
            else:
                kb_file = kb.get('file')
                sec = kb.get('section')
                if not kb_file or not sec:
                    errors.append(f'{gid} kbRef 必含 file + section')
                else:
                    full = os.path.join(ROOT, kb_file)
                    if not os.path.exists(full):
                        errors.append(f"{gid} kbRef.file={kb_file} 不存在")
                    else:
                        m = re.match(r'^§(\d+)', sec)
                        if not m:
                            errors.append(f'{gid} kbRef.section={sec} 格式非法（应如 §6 标题）')
                        elif not _kb_section_exists(full, int(m.group(1))):
                            errors.append(f"{gid} kbRef 章节 §{m.group(1)} 在 {kb_file} 中不存在")
        elif kb is not None and not isinstance(kb, dict):
            errors.append(f'{gid} kbRef 必须为对象 {{file, section}}')
        # 层级约束
        parent = g.get('parent')
        if level == 'vision' and parent:
            errors.append(f'{gid} vision 级 parent 必须为 null')
        if parent and parent not in gmap:
            errors.append(f'{gid} parent={parent} 在 goals 中不存在')
        # benchmark 结构
        bm = g.get('benchmark')
        if bm is not None:
            if not isinstance(bm, dict) or not bm.get('reference'):
                errors.append(f'{gid} benchmark 必含 reference 字段')
    # parent 链循环检测
    for g in goals:
        gid = g.get('id')
        seen = set()
        cur = gid
        while cur and cur in gmap:
            if cur in seen:
                errors.append(f'{gid} parent 链存在循环引用')
                break
            seen.add(cur)
            cur = gmap[cur].get('parent')

    # goalRef 前向可解析（严格）：tasks-index 里每个非空 goalRef 必须存在于 goals.json
    goal_ids = set(ids)
    idx = _load(INDEX_JSON)
    for t in idx.get('tasks', []):
        gr = t.get('goalRef')
        if gr and gr not in goal_ids:
            errors.append(f"{t['id']} goalRef={gr} 在 goals.json 中不存在")
    # relatedTasks 反向可解析（宽松）：引用的任务 id 必须存在于 tasks-index（历史任务 goalRef 可为 null，不强制相等）
    idx_ids = {t['id'] for t in idx.get('tasks', [])}
    for g in goals:
        for tid in (g.get('relatedTasks') or []):
            if tid not in idx_ids:
                errors.append(f"{g['id']} relatedTasks 引用了不存在的任务 {tid}")


def main() -> int:
    mode = 'all'
    if '--render' in sys.argv:
        mode = 'render'
    elif '--check' in sys.argv:
        mode = 'check'
    elif '--goals' in sys.argv:
        mode = 'goals'

    if mode == 'goals':
        sync_state()  # 先同步派生视图（卡为真值），保证 health/收敛基于准确状态
        errs = check()
        if errs:
            print('CONTRACT CHECK FAILED:')
            for e in errs:
                print('  -', e)
            return 1
        merge_goals()
        return 0

    if mode == 'check':
        errs = check()
        if errs:
            print('CONTRACT CHECK FAILED:')
            for e in errs:
                print('  -', e)
            return 1
        print(f'OK: active={len(_read_cards())} contract consistent')
        return 0

    if mode == 'render':
        errs = check()
        if errs:
            print('CONTRACT CHECK FAILED:')
            for e in errs:
                print('  -', e)
            return 1
        n = render_index()
        fi = render_findings_index()
        print(f'OK: INDEX.md rendered ({n} tasks), findings-index rebuilt ({fi})')
        return 0

    # all: sync_state → check → archive → render（+ findings-index 重建）
    sync_state()
    errs = check()
    if errs:
        print('CONTRACT CHECK FAILED:')
        for e in errs:
            print('  -', e)
        return 1
    r = archive()
    n = render_index()
    fi = render_findings_index()
    print(f'OK: archived {r["archived"]}, INDEX.md rendered ({n} tasks), findings-index rebuilt ({fi})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
