@echo off
setlocal
pushd "%~dp0"
set "cycle_csc=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%cycle_csc%" (
  echo ERROR: .NET Framework 4.x x64 compiler not found.
  popd
  exit /b 1
)
if not exist "dist" mkdir "dist"
"%cycle_csc%" /nologo /target:exe /platform:x64 /optimize+ /r:System.DirectoryServices.dll /out:dist\SharpADIDNSCycle.exe SharpADIDNSCycle.cs
set "cycle_result=%ERRORLEVEL%"
if not "%cycle_result%"=="0" (
  popd
  exit /b %cycle_result%
)
echo Built: dist\SharpADIDNSCycle.exe ^(Windows amd64, .NET Framework 4.x^)
popd
exit /b 0
