@echo off
REM ============================================================================
REM Compile l'oracle de disposition ETW avec les EN-TETES DU SDK INSTALLE.
REM Ce n'est pas une bibliotheque DrDXT : c'est l'outil qui empeche une erreur
REM d'interop de passer inapercue parce que « tout compile ».
REM ============================================================================
setlocal
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
  echo BUILD_ERROR vcvars64 introuvable
  exit /b 1
)
pushd "%~dp0"
cl /nologo /W3 /O2 etwlayout.c /Fe:etwlayout.exe advapi32.lib
set RC=%ERRORLEVEL%
popd
exit /b %RC%
