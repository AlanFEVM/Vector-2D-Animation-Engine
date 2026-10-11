@echo off
rem 一键启动本地百科：起 HTTP 服务器 + 打开浏览器
rem 不要直接双击 index.html（file:// 下 docsify 读不到 Markdown）
cd /d "%~dp0"
set PORT=4000
where python >nul 2>nul
if %errorlevel%==0 (
  start "" python -m http.server %PORT%
) else (
  where npx >nul 2>nul
  if %errorlevel%==0 (
    start "" npx --yes serve -l %PORT% .
  ) else (
    echo 未找到 python 或 npx，请先安装其一，再运行本脚本。
    pause
    exit /b 1
  )
)
timeout /t 2 >nul
start "" http://127.0.0.1:%PORT%/
echo 百科已启动：http://127.0.0.1:%PORT%/
echo 关闭本窗口不会停止服务器；要停止请在任务栏结束 python/http.server 进程。
pause
