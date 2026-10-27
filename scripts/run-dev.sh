#!/bin/bash
set -euo pipefail
task_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
task_dotnet="${IISMANAGER_DOTNET:-dotnet}"
task_agent_pid=""
task_console_pid=""
cleanup() {
  if [[ -n "$task_console_pid" ]]; then kill -TERM "$task_console_pid" 2>/dev/null || true; fi
  if [[ -n "$task_agent_pid" ]]; then kill -TERM "$task_agent_pid" 2>/dev/null || true; fi
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM
for task_project in Agent Console; do
  if [[ ! -f "$task_root/src/IISManager.$task_project/bin/Release/net10.0/IISManager.$task_project.dll" ]]; then
    echo '请先运行 dotnet build IISManagerTool.slnx -c Release。' >&2
    exit 1
  fi
done
mkdir -p "$task_root/work/demo-sites"
cd "$task_root/src/IISManager.Agent"
DOTNET_ENVIRONMENT=Development Agent__AllowedSiteRoots__0="$task_root/work/demo-sites" "$task_dotnet" bin/Release/net10.0/IISManager.Agent.dll --contentRoot "$task_root/src/IISManager.Agent" &
task_agent_pid=$!
cd "$task_root/src/IISManager.Console"
DOTNET_ENVIRONMENT=Development "$task_dotnet" bin/Release/net10.0/IISManager.Console.dll --contentRoot "$task_root/src/IISManager.Console" &
task_console_pid=$!
echo "Development 模拟控制台：http://127.0.0.1:7443；Agent：http://127.0.0.1:7444。按 Ctrl+C 停止。"
echo "本脚本子进程 PID：Agent=$task_agent_pid Console=$task_console_pid"
while kill -0 "$task_agent_pid" 2>/dev/null && kill -0 "$task_console_pid" 2>/dev/null; do sleep 1; done
exit 1
