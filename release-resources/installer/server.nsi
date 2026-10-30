Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
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
Var WantDesktop
Var WantCloseServer
Var UpdateLocksBusy
Var CloseAttempts
!define MUI_WELCOMEPAGE_TEXT "Install the Media Server and its browser interface for your Windows account. This does not install the separate Windows client.$\r$\n$\r$\nA Server Control window and tray icon let you start, stop or restart the server. First launch selects a media folder and offers verified FFprobe tools, then opens browser account setup.$\r$\n$\r$\nUpdates can migrate your database. Back up data before upgrading. Setup can close the managed server and control window for you. Uninstall keeps your media and private server data. Builds are unsigned early alphas."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Start Media Server (existing accounts and libraries are kept)"
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
!macro ProbeUpdateLease NAME
  ${If} ${FileExists} "$LOCALAPPDATA\Splortch\MediaServer\${NAME}"
    StrCpy $0 "$LOCALAPPDATA\Splortch\MediaServer\${NAME}"
    System::Call 'kernel32::CreateFileW(w r0, i 0xC0000000, i 0, p 0, i 3, i 0, p 0) p.r1 ?e'
    Pop $2
    ${If} $1 == -1
      ${If} $2 == 32
      ${OrIf} $2 == 33
        StrCpy $UpdateLocksBusy "1"
      ${Else}
        MessageBox MB_OK|MB_ICONEXCLAMATION "Cannot access the server data directory. Check permissions before updating. No application files have been changed." /SD IDOK
        SetErrorLevel 2
        Abort
      ${EndIf}
    ${Else}
      System::Call 'kernel32::CloseHandle(p r1)'
    ${EndIf}
  ${EndIf}
!macroend
Function ProbeUpdateLocks
  StrCpy $UpdateLocksBusy "0"
  !insertmacro ProbeUpdateLease ".server.lock"
  !insertmacro ProbeUpdateLease "tray-host.lock"
FunctionEnd
Function PrepareRunningUpdate
  Call ProbeUpdateLocks
  ${If} $UpdateLocksBusy != "1"
    Return
  ${EndIf}
  ${If} $WantCloseServer != "1"
    IfSilent decline_running_update
    MessageBox MB_YESNO|MB_ICONQUESTION "Media Server or its control window is running. Close the managed server and control window to continue updating?$\r$\n$\r$\nPlayback will be interrupted. Back up your server data before upgrading. On the Finish page, leave Start Media Server selected to start the updated server." /SD IDNO IDYES close_running_update
    Goto decline_running_update
  ${EndIf}
  close_running_update:
    ; Use the new bundled helper, not an arbitrary installed script or process name.
    InitPluginsDir
    File /oname=$PLUGINSDIR\MediaServer.UpdateStop.ps1 "${PAYLOAD}\MediaServer.Tray.ps1"
    ExecWait '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\MediaServer.UpdateStop.ps1" -Stop -NonInteractive' $3
    ${If} $3 != 0
      Goto could_not_close_update
    ${EndIf}
    StrCpy $CloseAttempts 0
    wait_for_update_locks:
      Call ProbeUpdateLocks
      ${If} $UpdateLocksBusy != "1"
        Return
      ${EndIf}
      IntOp $CloseAttempts $CloseAttempts + 1
      ${If} $CloseAttempts >= 100
        Goto could_not_close_update
      ${EndIf}
      Sleep 300
      Goto wait_for_update_locks
  decline_running_update:
    MessageBox MB_OK|MB_ICONINFORMATION "Update cancelled while the server is running. Choose Yes to let setup close Server Control, or exit it manually. Silent updates must explicitly use /CLOSESERVER. No application files have been changed." /SD IDOK
    SetErrorLevel 2
    Abort
  could_not_close_update:
    MessageBox MB_OK|MB_ICONEXCLAMATION "The server or controller is still holding its files. A separately launched server must be stopped in its original terminal or app. Close it and retry. No application files have been changed." /SD IDOK
    SetErrorLevel 2
    Abort
FunctionEnd
Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "This build requires Windows x64." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ReadRegDWORD $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer" "ReleaseNumber"
  ${If} $0 > ${BUILD}
    MessageBox MB_OK|MB_ICONSTOP "A newer server is installed. Downgrading can damage schema compatibility. Restore a matching backup separately instead." /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/DESKTOP" $1
  ${IfNot} ${Errors}
    StrCpy $WantDesktop "1"
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/CLOSESERVER" $1
  ${IfNot} ${Errors}
    StrCpy $WantCloseServer "1"
  ${EndIf}
  ClearErrors
  Call PrepareRunningUpdate
  !insertmacro CheckLease ".server.lock" $ServerLease
  !insertmacro CheckLease "tray-host.lock" $TrayLease
FunctionEnd
Section "Server and browser interface (required)"
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR\app"
  File /r "${PAYLOAD}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} $WantDesktop == "1"
  CreateShortcut "$DESKTOP\Media Server Control.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1" -Command Show'
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\Media Server"
  CreateShortcut "$SMPROGRAMS\Media Server\Media Server.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1"'
  CreateShortcut "$SMPROGRAMS\Media Server\Media Server Control.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1" -Command Show'
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
Section /o "Desktop shortcut (Media Server Control)"
  SetShellVarContext current
  CreateShortcut "$DESKTOP\Media Server Control.lnk" "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$INSTDIR\app\MediaServer.Tray.ps1" -Command Show'
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
  Delete "$DESKTOP\Media Server Control.lnk"
  Delete "$SMSTARTUP\Media Server.lnk"
  Delete "$SMPROGRAMS\Media Server\Media Server.lnk"
  Delete "$SMPROGRAMS\Media Server\Media Server Control.lnk"
  Delete "$SMPROGRAMS\Media Server\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Media Server"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
