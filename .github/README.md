# .github

GitHub 协作与自动化入口。

## 内容
- `workflows/`：兼容性检查、固定 0.107.1 Release 构建和正式 Release 自动化。
- `ISSUE_TEMPLATE/`：Bug / 功能请求表单。
- `PULL_REQUEST_TEMPLATE.md`：PR 验证与风险说明模板。

## 维护边界
- CI 只负责可重跑验证和发布编排，不在这里保存运行日志、问题包或本机路径。
- 工作流修改必须与 `source/build-target.json`、`source/docs/TEST_MATRIX.md` 的当前事实一致。
- 私有凭据只能使用 GitHub Secrets；不得写入仓库。
