# -*- coding: utf-8 -*-
"""
CloudPan 任务契约迁移：schema v2 (tasks.json 单文件) → v3 (contract/ 分片)

设计动机（实测：tasks.json 690KB ≈ 16.6 万 token，占 sonnet 窗口 83%；
executor/verifier 每任务读全量契约是任务间 58% 墙钟的根因）。
v3 拆分「活跃状态 / 单任务卡 / 历史 / findings / 全量索引」，
使 executor/verifier 只读单任务卡（~600 token），不再触碰全量。

本脚本只生成新结构，不删除/覆盖 v2 源文件（tasks.json 保留作归档与回退）。
幂等：目标目录已存在时需 --force 重建。

用法:
    python scripts/task-matrix/migrate-v2-to-v3.py            # 生成
    python scripts/task-matrix/migrate-v2-to-v3.py --force    # 重建
"""
import json
import os
import shutil
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
SRC = os.path.join(ROOT, 'docs', 'task-matrix', 'tasks.json')
DST = os.path.join(ROOT, 'docs', 'task-matrix', 'contract')
GENERATED_AT = '2026-08-04'  # 迁移执行日；重新迁移时如需更新可改此值


def main() -> int:
    with open(SRC, encoding='utf-8') as f:
        v2 = json.load(f)
    if v2.get('schemaVersion') != 2:
        print(f'源契约 schemaVersion={v2.get("schemaVersion")}，期望 2。中止。')
        return 1

    if os.path.exists(DST):
        if '--force' not in sys.argv:
            print(f'目标目录已存在: {DST}。用 --force 重建。')
            return 1
        shutil.rmtree(DST)

    os.makedirs(os.path.join(DST, 'active'))
    os.makedirs(os.path.join(DST, 'history'))

    tasks = v2['tasks']
    findings = v2['findings']
    batches = v2['batches']

    # 前置校验：id 唯一
    ids = [t['id'] for t in tasks]
    if len(ids) != len(set(ids)):
        print('❌ 任务 id 不唯一，中止。')
        return 1

    # 1. findings.json（全部，追溯用）
    with open(os.path.join(DST, 'findings.json'), 'w', encoding='utf-8') as f:
        json.dump(findings, f, ensure_ascii=False, indent=1)

    # 2. 按状态分流：done → 归档历史；活跃（todo/in-progress/acceptance）→ active/
    done_by_batch: dict[int, list] = {}
    active_tasks = []
    for t in tasks:
        if t['status'] == 'done':
            done_by_batch.setdefault(t['batch'], []).append(t)
        else:
            active_tasks.append(t)

    # 3. history/batch-{NN}.json（已闭合批次：结论 + 该批次 done 任务完整卡）
    for b in batches:
        bn = b['batch']
        entry = {
            'batch': bn,
            'date': b.get('date'),
            'conclusion': b.get('conclusion'),
            'dimensionSummary': b.get('dimensionSummary'),
            'tasks': done_by_batch.get(bn, []),
        }
        with open(os.path.join(DST, 'history', f'batch-{bn:02d}.json'), 'w', encoding='utf-8') as f:
            json.dump(entry, f, ensure_ascii=False, indent=1)

    # 4. active/T-{id}.json（单任务完整卡）+ state.json（活跃摘要）
    state = {'active': []}
    for t in sorted(active_tasks, key=lambda x: x['id']):
        with open(os.path.join(DST, 'active', f"{t['id']}.json"), 'w', encoding='utf-8') as f:
            json.dump(t, f, ensure_ascii=False, indent=1)
        state['active'].append({
            'id': t['id'],
            'title': t['title'],
            'dimension': t['dimension'],
            'priority': t['priority'],
            'status': t['status'],
            'batch': t['batch'],
            'dependsOn': t.get('dependsOn', []),
            'attempts': t.get('attempts', 0),
        })
    with open(os.path.join(DST, 'state.json'), 'w', encoding='utf-8') as f:
        json.dump(state, f, ensure_ascii=False, indent=1)

    # 5. tasks-index.json（全部任务一行摘要：渲染 INDEX + producer 跨批次去重）
    index = {'tasks': []}
    for t in sorted(tasks, key=lambda x: x['id']):
        index['tasks'].append({
            'id': t['id'],
            'title': t['title'],
            'dimension': t['dimension'],
            'priority': t['priority'],
            'status': t['status'],
            'batch': t['batch'],
            'location': t.get('location'),
        })
    with open(os.path.join(DST, 'tasks-index.json'), 'w', encoding='utf-8') as f:
        json.dump(index, f, ensure_ascii=False, indent=1)

    # 6. meta.json
    meta = {
        'schemaVersion': 3,
        'currentBatch': batches[-1]['batch'] if batches else 0,
        'generatedAt': GENERATED_AT,
        'totalTasks': len(tasks),
        'activeTasks': len(active_tasks),
        'totalFindings': len(findings),
    }
    with open(os.path.join(DST, 'meta.json'), 'w', encoding='utf-8') as f:
        json.dump(meta, f, ensure_ascii=False, indent=1)

    # ===== 校验 =====
    errors: list[str] = []

    # a) 任务总数：归档 + 活跃 = 总数
    archived_count = sum(len(v) for v in done_by_batch.values())
    if archived_count + len(active_tasks) != len(tasks):
        errors.append(f'归档 {archived_count} + 活跃 {len(active_tasks)} != 总数 {len(tasks)}')

    # b) findingId 均可解析
    fid = {f['id'] for f in findings}
    for t in tasks:
        if t.get('findingId') and t['findingId'] not in fid:
            errors.append(f"{t['id']} findingId={t['findingId']} 在 findings 中不存在")

    # c) active/ 文件集合与活跃任务一致
    active_files = set(os.listdir(os.path.join(DST, 'active')))
    expected_files = {t['id'] + '.json' for t in active_tasks}
    if active_files != expected_files:
        errors.append(f'active/ 文件不一致，差异: {sorted(active_files ^ expected_files)}')

    # d) state.json 与 active/ 卡一一对应
    with open(os.path.join(DST, 'state.json'), encoding='utf-8') as f:
        state_check = json.load(f)
    if len(state_check['active']) != len(active_tasks):
        errors.append(f"state.json 活跃 {len(state_check['active'])} != active/ 卡 {len(active_tasks)}")

    # e) tasks-index 覆盖全部任务
    with open(os.path.join(DST, 'tasks-index.json'), encoding='utf-8') as f:
        idx = json.load(f)
    if len(idx['tasks']) != len(tasks):
        errors.append(f"tasks-index {len(idx['tasks'])} != 总数 {len(tasks)}")

    if errors:
        print('❌ 迁移校验失败:')
        for e in errors:
            print('  -', e)
        return 1

    print('✅ 迁移完成并校验通过')
    print(f'  tasks: {len(tasks)}（done {archived_count} / 活跃 {len(active_tasks)}）')
    print(f'  findings: {len(findings)}')
    print(f'  batches: {len(batches)}（history 文件 {len(batches)} 个）')
    print(f'  输出: {DST}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
