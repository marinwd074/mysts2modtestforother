# workflows

GitHub Actions 工作流目录。

- `compatibility.yml`：普通 push 的通用轻量门禁；只跑一个 Linux 结构/版本检查。
- `phase-c-validation.yml`：仅当 `main` 上 R0/transition 相关路径变化时自动触发的定向 Windows pinned 回归，不是所有 push 的通用门禁。
- `pinned-release-build.yml`：固定 STS2 0.107.1 / RitsuLib 的完整 Release、E0/U0/U1/U2/P0/P1 验证；仅手动触发。
- `pinned-monster-target-audit.yml`：固定版本怪物目标语义审计；仅手动触发。
- `release.yml`：仅版本 tag 触发，构建并生成 draft GitHub Release。

原则：日常提交默认只做快速结构门禁；只有命中专门路径时才自动跑定向 Windows pinned 回归，其余耗时 Windows、LFS、完整 pinned harness 和 IL 审计按需手动运行。
