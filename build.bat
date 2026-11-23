@echo off
rem Compile avec le csc de .NET Framework 4.8 fourni par Windows (aucun SDK a installer).
rem "build.bat dev" ajoute les outils de developpement (--snapshot, --demo, --listen, --lang).
cd /d "%~dp0"
set DEF=
if /i "%1"=="dev" set DEF=/define:DEV
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /out:PadSwitch.exe %DEF% ^
  /win32icon:assets\padswitch.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /resource:fonts\Geist-Regular.ttf,Geist-Regular.ttf /resource:fonts\Geist-Medium.ttf,Geist-Medium.ttf ^
  PadSwitch.cs