@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /define:TEST /main:Test /out:"%~dp0LockdownTest.exe" "%~dp0Lockdown.cs" "%~dp0Test.cs" || exit /b 1
"%~dp0LockdownTest.exe"
