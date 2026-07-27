; Renzo Shiori desktop client installer (per-user, no admin required)
Unicode true
!define APPNAME "Renzo Shiori"
!define VERSION "1.2.1"
!define SRC "/tmp/renzo-exe-folder"

Name "${APPNAME}"
OutFile "/export/Main/Renzo-out/RenzoShiori-Setup.exe"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
Icon "/opt/zurg-stack/Rensaio/clients/windows/renzo.ico"
UninstallIcon "/opt/zurg-stack/Rensaio/clients/windows/renzo.ico"
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!define MUI_ICON "/opt/zurg-stack/Rensaio/clients/windows/renzo.ico"
!define MUI_UNICON "/opt/zurg-stack/Rensaio/clients/windows/renzo.ico"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\RenzoShiori.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"

Section "Install"
  SetOutPath "$INSTDIR"
  File /r "${SRC}/*"
  CreateShortcut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\RenzoShiori.exe" "" "$INSTDIR\RenzoShiori.exe" 0
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\RenzoShiori.exe" "" "$INSTDIR\RenzoShiori.exe" 0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\RenzoShiori.exe"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
SectionEnd
