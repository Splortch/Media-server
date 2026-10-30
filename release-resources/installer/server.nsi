Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef PAYLOAD
  !error "PAYLOAD is required"
!endif
!ifndef OUTPUT
  !error "OUTPUT is required"
!endif
Name "Media Server"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Splortch\MediaServer"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
Var ServerLease
Var TrayLease
!define MUI_WELCOMEPAGE_TEXT "Install the Media Server and its browser interface for your Windows account. This does not install the separate Windows client.$\r$\n$\r$\nA Server Control window and tray icon let you start, stop or restart the server. First launch selects a media folder and offers verified FFprobe tools, then opens browser account setup.$\r$\n$\r$\nUpdates can migrate your database. Stop the server and back up data before upgrading. Uninstall keeps your media and private server data. Builds are unsigned early alphas."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Start Media Server and open first-time setup"
!define MUI_FINISHPAGE_RUN_FUNCTION Launch
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PAYLOAD}\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro CheckLease NAME HANDLE
  ${If} ${FileExists} "$LOCALAPPDATA\Splortch\MediaServer\${NAME}"
  StrCpy $0 "$LOCALAPPDATA\Splortch\MediaServer\${NAME}"
  System::Call 'kernel32::CreateFileW(w r0, i 0xC0000000, i 0, p 0, i 3, i 0, p 0) p.r1'
  ${If} $1 == -1
    MessageBox MB_OK|MB_ICONEXCLAMATION "Choose Stop server and exit control from the tray menu (or close the portable server) before installing or uninstalling. No files have been changed." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  StrCpy ${HANDLE} $1
  ${EndIf}
!macroend
Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "This build requires Windows x64." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  !insertmacro CheckLease ".server.lock" $ServerLease
  !insertmacro CheckLease "tray-host.lock" $TrayLease
  ReadRegDWORD $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "ReleaseNumber"
  ${If} $0 > ${BUILD}
    MessageBox MB_OK|MB_ICONSTOP "A newer server is installed. Downgrading can damage schema compatibility. Restore a matching backup separately instead." /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  ClearErrors
FunctionEnd
Section "Server and browser interface (required)"
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR\app"
  File /r "${PAYLOAD}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Media Server"
  CreateShortcut "$SMPROGRAMS\Media Server\Media Server.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1"'
  CreateShortcut "$SMPROGRAMS\Media Server\Media Server Control.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1"'
  CreateShortcut "$SMPROGRAMS\Media Server\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "DisplayName" "Media Server (server)"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "DisplayVersion" "${VERSION}"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "ReleaseNumber" ${BUILD}
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "Publisher" "Splortch"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "NoRepair" 1
SectionEnd
Section /o "Start automatically when I sign in"
  CreateShortcut "$SMSTARTUP\Media Server.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1" -NoBrowser'
SectionEnd
Function Launch
  Call ReleaseLeases
  Exec '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1"'
FunctionEnd
Function ReleaseLeases
  ${If} $ServerLease != ""
    System::Call 'kernel32::CloseHandle(p $ServerLease)'
    StrCpy $ServerLease ""
  ${EndIf}
  ${If} $TrayLease != ""
    System::Call 'kernel32::CloseHandle(p $TrayLease)'
    StrCpy $TrayLease ""
  ${EndIf}
FunctionEnd
Function .onGUIEnd
  Call ReleaseLeases
FunctionEnd
Function un.onInit
  SetShellVarContext current
  SetRegView 64
  !insertmacro CheckLease ".server.lock" $ServerLease
  !insertmacro CheckLease "tray-host.lock" $TrayLease
FunctionEnd
Section "Uninstall"
  SetShellVarContext current
  ; Remove only files from the packaged manifest. Never recurse into user data.
  !include "${UNINSTALL_MANIFEST}"
  Delete "$SMSTARTUP\Media Server.lnk"
  Delete "$SMPROGRAMS\Media Server\Media Server.lnk"
  Delete "$SMPROGRAMS\Media Server\Media Server Control.lnk"
  Delete "$SMPROGRAMS\Media Server\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Media Server"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
