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
INDEX_MD = os.path.join(ROOT, 'docs', 'task-matrix', 'INDEX.md')

DIM_LABEL = {'architecture': '架构', 'function': '功能', 'simplicity': '技术简洁', 'ux': 'UX'}
STATUS_LABEL = {'todo': '待办', 'in-progress': '进行中', 'acceptance': '待验收', 'done': '已完成'}


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
    """从 tasks-index.json + meta.json 渲染 INDEX.md（活状态板）。"""
    idx = _load(INDEX_JSON)
    meta = _load(META)
    tasks = idx.get('tasks', [])

    n_done = sum(1 for t in tasks if t['status'] == 'done')
    n_acc = sum(1 for t in tasks if t['status'] == 'acceptance')
    n_act = sum(1 for t in tasks if t['status'] in ('in-progress', 'todo'))

    lines = [
        '# CloudPan 任务矩阵 — 状态板',
        '',
        f"> 契约: docs/task-matrix/contract/（schemaVersion=3）｜ 更新: {meta.get('generatedAt', '')}",
        f"> 统计: {len(tasks)} 任务 = {n_done} done / {n_acc} 待验收 / {n_act} 待办",
        '',
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


def check():
    """一致性校验：active 卡 status 非 done、state/tasks-index/active 三方一致、id 唯一。"""
    errors = []
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


def main() -> int:
    mode = 'all'
    if '--render' in sys.argv:
        mode = 'render'
    elif '--check' in sys.argv:
        mode = 'check'

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
