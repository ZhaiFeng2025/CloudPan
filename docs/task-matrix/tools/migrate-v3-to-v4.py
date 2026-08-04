# -*- coding: utf-8 -*-
"""
CloudPan 任务契约迁移：schema v3 (分片) → v4 (目标模块)

v4 变更内容（就地增量迁移，不重建目录）：
1. 新增 contract/goals.json —— 目标契约（目标设定 + 量化分解的唯一来源），初始空
2. 任务卡与 tasks-index 行新增可选字段 goalRef（关联目标 G-###；历史任务补 null）
3. meta.json schemaVersion 3 → 4

v3→v4 向后兼容：goals.json 是纯新增文件、goalRef 是可选字段，
即便旧工具按 v3 读也能运行（goalRef 缺失按 null 处理）。

幂等：已 v4（meta.schemaVersion==4 且 goals.json 存在）时提示并退出，可安全重复运行。

用法:
    python docs/task-matrix/tools/migrate-v3-to-v4.py
"""
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
CONTRACT = os.path.join(ROOT, 'docs', 'task-matrix', 'contract')
META = os.path.join(CONTRACT, 'meta.json')
GOALS = os.path.join(CONTRACT, 'goals.json')
HISTORY = os.path.join(CONTRACT, 'history')
INDEX_JSON = os.path.join(CONTRACT, 'tasks-index.json')


def _load(path):
    with open(path, encoding='utf-8') as f:
        return json.load(f)


def _save(path, data):
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=1)


def main() -> int:
    meta = _load(META)

    # 幂等检查：已 v4 直接退出
    if os.path.exists(GOALS) and meta.get('schemaVersion') == 4:
        print('[OK] 已是 schemaVersion=4（goals.json 已存在），无需迁移。')
        return 0

    if meta.get('schemaVersion') != 3:
        print(f"[FAIL] meta.schemaVersion={meta.get('schemaVersion')}，期望 3。中止。")
        return 1

    # 1. 写 goals.json（初始空，由 /mission --goals 设定填充）
    _save(GOALS, {'schemaVersion': 4, 'goals': []})

    # 2. 历史批次任务卡补 goalRef（可选字段，向后兼容）
    hist_goals = 0
    if os.path.isdir(HISTORY):
        for fn in sorted(os.listdir(HISTORY)):
            if not fn.endswith('.json'):
                continue
            hp = os.path.join(HISTORY, fn)
            entry = _load(hp)
            for t in entry.get('tasks', []):
                if 'goalRef' not in t:
                    t['goalRef'] = None
                    hist_goals += 1
            _save(hp, entry)

    # 3. tasks-index.json 行补 goalRef
    idx = _load(INDEX_JSON)
    idx_goals = 0
    for item in idx.get('tasks', []):
        if 'goalRef' not in item:
            item['goalRef'] = None
            idx_goals += 1
    _save(INDEX_JSON, idx)

    # 4. meta.json schemaVersion → 4
    meta['schemaVersion'] = 4
    _save(META, meta)

    print('[OK] 迁移完成（v3 → v4）')
    print(f'  goals.json: 已创建（空目标集）')
    print(f'  history 任务卡补 goalRef: {hist_goals} 条')
    print(f'  tasks-index 补 goalRef: {idx_goals} 条')
    print('  下一步: 运行 python docs/task-matrix/tools/archive.py --check 校验')
    return 0


if __name__ == '__main__':
    sys.exit(main())
