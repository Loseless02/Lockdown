@echo off
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32manifest:"%~dp0app.manifest" /out:"%~dp0Lockdown.exe" "%~dp0Lockdown.cs"
