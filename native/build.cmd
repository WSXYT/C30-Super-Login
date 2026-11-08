@echo off
setlocal
if not defined C30_VCVARS for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "C30_VCVARS=%%i\VC\Auxiliary\Build\vcvars32.bat"
if not defined C30_VCVARS exit /b 3
call "%C30_VCVARS%" >nul
if errorlevel 1 exit /b %errorlevel%
if not exist "%~dp0..\artifacts\native" mkdir "%~dp0..\artifacts\native"
pushd "%~dp0..\artifacts\native"
cl /nologo /std:c++17 /O2 /MT /W4 /LD "%~dp0agent.cpp" /link /OUT:C30Agent.dll user32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 "%~dp0helper.cpp" /link /OUT:C30Native.exe user32.lib advapi32.lib ole32.lib
if errorlevel 1 exit /b %errorlevel%
if not exist fixture mkdir fixture
pushd fixture
cl /nologo /std:c++17 /O2 /MT /W4 /LD "%~dp0fixture_dll.cpp" /link /DEF:"%~dp0fixture.def" /OUT:DuiLib.dll user32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /O2 /MT /W4 "%~dp0fixture.cpp" /link /OUT:C30Fixture.exe user32.lib
if errorlevel 1 exit /b %errorlevel%
popd
popd
