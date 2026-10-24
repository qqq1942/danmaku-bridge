#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
#  把本仓库推到 GitHub
#
#  用法：
#    ./push-to-github.sh             交互式输入仓库地址和 PAT
#    ./push-to-github.sh --dry-run   只检查环境，不推送
#
#  PAT 只在这一条命令里用，不写进 .git/config、不落盘、回显时打码。
# ═══════════════════════════════════════════════════════════════
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")" || exit 1

DRY=0
[ "${1:-}" = "--dry-run" ] && DRY=1

say() { printf '%s\n' "$*"; }
mask() { sed 's|//[^@/]*@|//***@|g'; }

say "═══ 推送前检查 ═══"
say "  目录    : $(pwd)"
say "  分支    : $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"
say "  待推提交: $(git rev-list --count HEAD 2>/dev/null || echo 0) 个"
say "  工作区  : $([ -z "$(git status --porcelain)" ] && echo '干净 ✅' || echo '有未提交改动 ⚠️')"
say ""

if [ -n "$(git status --porcelain)" ]; then
  say "⚠️ 有未提交的改动，请先 git add -A && git commit -m '...'"
  [ "$DRY" = "1" ] || exit 1
fi

# 提交前再确认一次没有密钥
if git ls-files | xargs -r grep -lE 'SESSDATA="[a-zA-Z0-9%]{20,}|NAPCAT_TOKEN=[A-Za-z0-9]{10,}' 2>/dev/null | grep -q .; then
  say "❌ 仓库里有疑似密钥，已中止："
  git ls-files | xargs -r grep -lE 'SESSDATA="[a-zA-Z0-9%]{20,}|NAPCAT_TOKEN=[A-Za-z0-9]{10,}' 2>/dev/null | sed 's/^/     /'
  exit 2
fi
say "密钥扫描: ✅ 干净"

if [ "$DRY" = "1" ]; then
  say ""
  say "环境检查完成（--dry-run，未推送）"
  exit 0
fi

say ""
printf '仓库 HTTPS 地址 (https://github.com/用户名/仓库.git)：'
read -r URL
URL="${URL// /}"
case "$URL" in
  https://*.git) ;;
  https://*)     URL="${URL%/}.git" ;;
  *) say "❌ 地址必须是 https:// 开头并以 .git 结尾"; exit 3 ;;
esac

printf 'Personal Access Token（输入不回显）：'
read -rs PAT
echo
[ -n "$PAT" ] || { say "❌ token 为空"; exit 3; }

HOSTPATH="${URL#https://}"
AUTH_URL="https://${PAT}@${HOSTPATH}"

say ""
say "═══ 试探仓库是否可达 ═══"
if git ls-remote "$AUTH_URL" HEAD >/dev/null 2>&1; then
  say "  ✅ 认证成功，仓库可访问"
else
  say "  ❌ 认证失败。常见原因："
  say "     · token 过期 / 权限不足（经典版需勾 repo，Fine-grained 需 Contents: Read and write）"
  say "     · 仓库地址写错（用户名/仓库名大小写敏感）"
  say "     · 仓库还不存在 —— 先去 GitHub 新建空仓库"
  exit 4
fi

say ""
say "═══ 推送 ═══"
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
if git -c credential.helper= push "$AUTH_URL" "HEAD:main" --tags 2>&1 | mask | sed 's/^/  /'; then
  :
fi
PUSH_RC=${PIPESTATUS[0]}

# 配置一个干净的 remote（不含 token），方便以后直接 git push
git remote remove origin 2>/dev/null || true
git remote add origin "$URL"

say ""
if [ "$PUSH_RC" = "0" ]; then
  say "✅ 推送成功：${URL}"
  say "   分支 ${BRANCH} → main"
  say "   打开 ${URL%.git} 查看"
else
  say "⚠️ 推送返回码 ${PUSH_RC}，若是 'everything up-to-date' 可忽略"
fi

say ""
say "═══ 收尾提醒 ═══"
say "  · token 没有写进任何文件（git config 里只有不含 token 的 origin）"
say "  · 建议现在就去 GitHub 删掉这个 token：Settings → Developer settings → Tokens"
say "  · 验证：git ls-remote --heads origin"
