# -*- coding: utf-8 -*-
"""
CloudPan 任务契约聚合工具（v3）：归档 + 渲染。

设计动机：executor/verifier 只读写 active/T-{id}.json 单卡；「done 卡归档到 history、
更新 state.json / tasks-index.json、渲染 INDEX.md」等聚合操作由本脚本幂等完成，
避免 AI 手改多文件 JSON 出错。

用法:
    python scripts/task-matrix/archive.py            # 归档 done 卡 + 更新索引 + 渲染 INDEX.md
    python scripts/task-matrix/archive.py --render   # 仅重渲染 INDEX.md（不归档）
    python scripts/task-matrix/archive.py --check    # 只校验契约一致性，不改动
    python scripts/task-matrix/archive.py --goals    # 合并 .reviews/goals/ 度量结果 + 渲染 INDEX.md（v4）
"""
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
CONTRACT = os.path.join(ROOT, 'docs', 'task-matrix', 'contract')
ACTIVE = os.path.join(CONTRACT, 'active')
HISTORY = os.path.join(CONTRACT, 'history')
STATE = os.path.join(CONTRACT, 'state.json')
INDEX_JSON = os.path.join(CONTRACT, 'tasks-index.json')
META = os.path.join(CONTRACT, 'meta.json')
FINDINGS = os.path.join(CONTRACT, 'findings.json')
GOALS = os.path.join(CONTRACT, 'goals.json')
REVIEWS = os.path.join(ROOT, 'docs', 'task-matrix', '.reviews')
GOALS_REVIEW = os.path.join(REVIEWS, 'goals')
INDEX_MD = os.path.join(ROOT, 'docs', 'task-matrix', 'INDEX.md')

DIM_LABEL = {'architecture': '架构', 'function': '功能', 'simplicity': '技术简洁', 'ux': 'UX'}
STATUS_LABEL = {'todo': '待办', 'in-progress': '进行中', 'acceptance': '待验收', 'done': '已完成'}
GOAL_STATUS_LABEL = {'active': '进行中', 'achieved': '已达成', 'parked': '暂停', 'archived': '已归档'}
GOAL_DIR_LABEL = {'down': '↓', 'up': '↑', 'flat': '≈'}


def _load(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def _save(path, data):
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=1)


def _read_cards():
    """读 active/ 下全部任务卡，返回 {id: task}。"""
    cards = {}
    if not os.path.isdir(ACTIVE):
        return cards
    for fn in sorted(os.listdir(ACTIVE)):
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
    for t in sorted(tasks, key=lambda x: x['id']):
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
        '| ID | 维度 | 指标 | 基线 | 当前值 | 目标 | 方向 | 状态 | 关联任务 |',
        '|---|---|---|---|---|---|---|---|---|',
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
        dim = DIM_LABEL.get(g.get('dimension'), g.get('dimension')) if g.get('dimension') else '—'
        m = g.get('metric', {})
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
        rel = ','.join(by_goal.get(g['id'], [])) or '—'
        lines.append(f"| {prefix}{g['id']} | {dim} | {metric_str} | {base} | {cur} | {tgt} | {dir_} | {st} | {rel} |")
        for k in kids:
            render_tree(k, depth + 1)

    for r in roots:
        render_tree(r, 0)
    lines.append('')
    return lines


def _met_target(g):
    """按 direction 判定 currentValue 是否达到 target。"""
    cur = g.get('currentValue')
    tgt = g.get('target')
    if cur is None or tgt is None:
        return False
    d = g.get('direction', 'flat')
    if d == 'down':
        return cur <= tgt
    if d == 'up':
        return cur >= tgt
    return abs(cur - tgt) <= max(1, abs(tgt) * 0.01)


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

    merged = 0
    for r in reviews:
        for item in r.get('goals', []):
            g = goals.get(item['id'])
            if not g or not item.get('measured'):
                continue
            if g.get('level') == 'vision':
                continue  # 愿景级不度量不判定（无 currentValue），由子目标派生
            g['currentValue'] = item['currentValue']
            if item.get('lastMeasuredAt'):
                g['lastMeasuredAt'] = item['lastMeasuredAt']
            if item.get('measureNote'):
                g['measureNote'] = item['measureNote']
            if g['status'] == 'active' and _met_target(g):
                if g.get('measure', {}).get('type') == 'command':
                    g['status'] = 'achieved'
                else:
                    note = g.get('measureNote') or ''
                    g['measureNote'] = (note + ' | ' if note else '') + '已达标，待人工确认'
            merged += 1
    _save(GOALS, goals_data)
    n = render_index()
    print(f'OK: merged {merged} goal measurements, INDEX.md rendered ({n} tasks)')
    return {'merged': merged}


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
    for g in goals:
        gid = g.get('id')
        if not gid:
            errors.append('存在缺 id 的 goal')
            continue
        level = g.get('level', 'metric')
        if level not in LEVELS:
            errors.append(f'{gid} level={level} 非法（vision|domain|metric）')
        is_org = level in ('vision', 'domain')  # 组织层：可量化也可不量化
        if not is_org and g.get('dimension') not in DIM_LABEL:
            errors.append(f"{gid} dimension={g.get('dimension')} 非法")
        if not is_org and g.get('direction') not in GOAL_DIR_LABEL:
            errors.append(f"{gid} direction={g.get('direction')} 非法（组织层可空）")
        if g.get('status') not in GOAL_STATUS_LABEL:
            errors.append(f"{gid} status={g.get('status')} 非法")
        mt = g.get('measure', {}).get('type')
        if not is_org and mt not in ('command', 'assess'):
            errors.append(f"{gid} measure.type={mt} 非法（command|assess，组织层可空）")
        if not is_org and g.get('target') is None:
            errors.append(f'{gid} 缺 target（组织层可空）')
        if not is_org and mt == 'assess' and not g.get('measure', {}).get('rubric'):
            errors.append(f'{gid} assess 目标必含 measure.rubric')
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
        for tid in g.get('relatedTasks', []):
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
        n = render_index()
        print(f'OK: INDEX.md rendered ({n} tasks)')
        return 0

    # all: check → archive → render
    errs = check()
    if errs:
        print('CONTRACT CHECK FAILED:')
        for e in errs:
            print('  -', e)
        return 1
    r = archive()
    n = render_index()
    print(f'OK: archived {r["archived"]}, INDEX.md rendered ({n} tasks)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
