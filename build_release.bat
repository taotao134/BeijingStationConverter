@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "OUTPUT=%ROOT%ReleasePackages\北京站变频器-win-x64"

echo [1/3] 正在检查并关闭运行中的上位机进程...
taskkill /F /IM "北京站变频器.exe" >nul 2>&1

echo [2/3] 正在执行独立单文件发布 (Release win-x64)...
dotnet publish "%ROOT%BeijingStationConverter.csproj" -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o "%OUTPUT%"

if %ERRORLEVEL% equ 0 (
    del /f /q "%OUTPUT%\*.pdb" >nul 2>&1
    echo [3/3] 上位机单文件 EXE 打包成功！
    echo 输出路径: %OUTPUT%\北京站变频器.exe
) else (
    echo [3/3] 编译打包失败，错误码: %ERRORLEVEL%
)

endlocal
pause
