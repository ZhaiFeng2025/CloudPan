# -*- coding: utf-8 -*-
"""
CloudPan 契约 v3 端到端验证：模拟一个任务卡完整生命周期并校验链路。

模拟对象：T-999（batch 1，测试专用）
链路：producer 产出 → executor 推进(todo→in-progress→acceptance) → verifier 置 done
     → archive.py 归档(done 卡入 history、state 移除、tasks-index 更新、INDEX 渲染)
→ 清理：移除 T-999 全部痕迹，恢复干净契约。

用法: python scripts/task-matrix/verify-e2e.py
"""
import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
CONTRACT = os.path.join(ROOT, 'docs', 'task-matrix', 'contract')
ACTIVE = os.path.join(CONTRACT, 'active')
HISTORY = os.path.join(CONTRACT, 'history')
STATE = os.path.join(CONTRACT, 'state.json')
INDEX_JSON = os.path.join(CONTRACT, 'tasks-index.json')
ARCHIVE_PY = os.path.join(ROOT, 'docs', 'task-matrix', 'tools', 'archive.py')

TID = 'T-999'


def _load(p):
    with open(p, encoding='utf-8') as f:
        return json.load(f)


def _save(p, d):
    with open(p, 'w', encoding='utf-8') as f:
        json.dump(d, f, ensure_ascii=False, indent=1)


def _run(args):
    r = subprocess.run([sys.executable, ARCHIVE_PY] + args,
                       cwd=ROOT, capture_output=True, text=True, encoding='utf-8')
    if r.returncode != 0:
        print(f'  [archive {args}] FAILED:\n{r.stdout}\n{r.stderr}')
        sys.exit(1)
    return r.stdout.strip()


def cleanup():
    """移除 T-999 全部痕迹。"""
    p = os.path.join(ACTIVE, f'{TID}.json')
    if os.path.exists(p):
        os.remove(p)
    state = _load(STATE)
    state['active'] = [a for a in state.get('active', []) if a['id'] != TID]
    _save(STATE, state)
    idx = _load(INDEX_JSON)
    idx['tasks'] = [t for t in idx.get('tasks', []) if t['id'] != TID]
    _save(INDEX_JSON, idx)
    for fn in os.listdir(HISTORY):
        if not fn.endswith('.json'):
            continue
        hp = os.path.join(HISTORY, fn)
        e = _load(hp)
        e['tasks'] = [t for t in e.get('tasks', []) if t['id'] != TID]
        _save(hp, e)


def main():
    cleanup()  # 幂等：先清理上次残留
    print('== 1. producer 产出：写 active 卡 + state/tasks-index 追加 ==')

    # 1a. active 卡（完整卡，status=todo）
    card = {
        'id': TID, 'title': '验证用临时任务（生命周期测试，验证后删除）',
        'dimension': 'function', 'priority': 'P2', 'status': 'todo',
        'batch': 1, 'findingId': 'F-01', 'dependsOn': [],
        'location': 'scripts/task-matrix/verify-e2e.py', 'scope': '测试',
        'requirements': ['临时任务：仅用于验证契约 v3 生命周期'],
        'goal': '验证 todo→in-progress→acceptance→done→归档 全链路',
        'acceptanceCriteria': [{'text': '生命周期链路可跑通', 'verification': '自动',
                                'command': 'python scripts/task-matrix/verify-e2e.py'}],
        'attempts': 0, 'statusReason': None, 'note': None, 'updatedAt': '2026-08-04',
    }
    _save(os.path.join(ACTIVE, f'{TID}.json'), card)
    # 1b. state 追加
    state = _load(STATE)
    state['active'].append({'id': TID, 'title': card['title'], 'dimension': 'function',
                            'priority': 'P2', 'status': 'todo', 'batch': 1,
                            'dependsOn': [], 'attempts': 0})
    _save(STATE, state)
    # 1c. tasks-index 追加
    idx = _load(INDEX_JSON)
    idx['tasks'].append({'id': TID, 'title': card['title'], 'dimension': 'function',
                         'priority': 'P2', 'status': 'todo', 'batch': 1,
                         'location': card['location']})
    _save(INDEX_JSON, idx)

    # 1d. 归档脚本一致性检查应通过
    print('  [archive --check]', _run(['--check']))

    print('== 2. executor 推进：todo → in-progress → acceptance（写卡） ==')
    card = _load(os.path.join(ACTIVE, f'{TID}.json'))
    card['status'] = 'in-progress'
    _save(os.path.join(ACTIVE, f'{TID}.json'), card)
    card['status'] = 'acceptance'
    card['note'] = '执行：改动 X/Y/Z；验证 dotnet build 0 错误。'
    _save(os.path.join(ACTIVE, f'{TID}.json'), card)

    print('== 3. verifier 置 done（写卡，不归档） ==')
    card = _load(os.path.join(ACTIVE, f'{TID}.json'))
    assert card['status'] == 'acceptance', 'verifier 应只验收 acceptance 卡'
    card['status'] = 'done'
    card['note'] += '\n验收结论：独立裁决通过。'
    _save(os.path.join(ACTIVE, f'{TID}.json'), card)

    print('== 4. archive.py 归档 + 渲染 INDEX ==')
    print('  [archive --check]', _run(['--check']))
    print('  [archive]', _run([]))

    # 校验归档结果
    ok = True
    if os.path.exists(os.path.join(ACTIVE, f'{TID}.json')):
        print('  FAIL: T-999 仍在 active/')
        ok = False
    state = _load(STATE)
    if any(a['id'] == TID for a in state['active']):
        print('  FAIL: T-999 仍在 state.json')
        ok = False
    hist = _load(os.path.join(HISTORY, 'batch-01.json'))
    if not any(t['id'] == TID for t in hist['tasks']):
        print('  FAIL: T-999 未归档到 history/batch-01.json')
        ok = False
    idx = _load(INDEX_JSON)
    entry = next((t for t in idx['tasks'] if t['id'] == TID), None)
    if not entry or entry['status'] != 'done':
        print('  FAIL: tasks-index 中 T-999 状态非 done')
        ok = False
    index_md = open(os.path.join(ROOT, 'docs', 'task-matrix', 'INDEX.md'), encoding='utf-8').read()
    t999_line = next((ln for ln in index_md.split('\n') if ln.startswith('| T-999 |')), '')
    if not t999_line or '已完成' not in t999_line:
        print('  FAIL: INDEX.md 未渲染 T-999 为已完成')
        ok = False

    print('== 5. 清理 T-999 痕迹，恢复干净契约 ==')
    cleanup()
    _run(['--render'])
    print('  [archive --check]', _run(['--check']))

    if ok:
        print('ALL PASS: 契约 v3 生命周期端到端可用')
        return 0
    print('SOME CHECKS FAILED')
    return 1


if __name__ == '__main__':
    sys.exit(main())
