# CloudPan 审查知识库

供任务产生者 task-producer 及其审查子 Agent 使用的专业审查依据。每份知识库 = 行业标准 + 项目绑定约束 + 审查问题清单。

## 目录

| 维度 | 文件 | 标准依据 |
|---|---|---|
| 架构 | [architecture-kb.md](architecture-kb.md) | 整洁架构、SOLID、契约驱动 |
| 功能 | [feature-kb.md](feature-kb.md) | 家庭云盘功能域、竞品参照 |
| UX | [ux-kb.md](ux-kb.md) | Nielsen 启发式、WCAG 2.1 |
| 网盘设计 | [clouddrive-kb.md](clouddrive-kb.md) | 网盘产品形态、页面设计、交互（竞品参照） |
| 视觉美化 | [visual-design-kb.md](visual-design-kb.md) | 设计令牌、配色、字体、层次、状态色、深色模式 |
| 技术简洁 | [simplicity-kb.md](simplicity-kb.md) | YAGNI、KISS、SRP |
| 安全 | [security-kb.md](security-kb.md) | OWASP ASVS L1 |

## 使用约定

- task-producer 在 Step 2 分发审查子 Agent 时，必须要求子 Agent **先读对应维度知识库，再按其清单审查**
- 知识库是审查的**下限**不是上限：子 Agent 可在清单之外发现新问题
- 新增审查维度时，先在本目录补知识库，再更新 task-producer 的分发表
