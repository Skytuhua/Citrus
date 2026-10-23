; Runtime updater: this downloaded installer is the external update host.
; No runtime shell, PowerShell, process killing or recursive directory deletion.
Var AutomaticUpdate
Var UpdateParentPid
Var UpdateParentStart
Var PreviousInventory
Var UpdateParentCompleted
Var UpdateRestartAttempted
Var UpdateParentHandle
Var UpdateTimeBuffer
Var UpdateActualStart
Var UpdateBackupPath
Var UpdateOldInventory
Var UpdateNewInventory
Var UpdateOldSdk
Var UpdateOldVersion
Var UpdateFailedPath
Var UpdateFailedIndex
Var UpdateState
Var UpdateInProgress
Var RollbackInProgress
Var HashPath
Var HashValue
Var HashFileHandle
Var HashAlgorithm
Var HashContext
Var HashBuffer
Var HashDigest
Var HashRead

!macro HashFunction PREFIX
Function ${PREFIX}HashFile
  Pop $HashPath
  StrCpy $HashValue ""
  StrCpy $HashFileHandle -1
  System::Call 'kernel32::CreateFileW(w "$HashPath", i 0x80000000, i 1, p 0, i 3, i 0x08000000, p 0) p.r0'
  StrCpy $HashFileHandle $0
  ${If} $HashFileHandle == -1
    Goto hash_cleanup
  ${EndIf}
  ${If} $HashAlgorithm == 0
  ${OrIf} $HashAlgorithm == ""
    System::Call 'bcrypt::BCryptOpenAlgorithmProvider(*p.r0, w "SHA256", p 0, i 0x20) i.r1'
    StrCpy $HashAlgorithm $0
    ${If} $1 != 0
      StrCpy $HashAlgorithm 0
      Goto hash_cleanup
    ${EndIf}
  ${EndIf}
  ${If} $HashContext == 0
  ${OrIf} $HashContext == ""
    ; Successful BCryptFinishHash resets this reusable context for the next file.
    System::Call 'bcrypt::BCryptCreateHash(p $HashAlgorithm, *p.r0, p 0, i 0, p 0, i 0, i 0x20) i.r1'
    ${If} $1 != 0
      StrCpy $HashContext 0
      Goto hash_cleanup
    ${EndIf}
    StrCpy $HashContext $0
  ${EndIf}
  ${If} $HashBuffer == 0
  ${OrIf} $HashBuffer == ""
    System::Alloc 1048576
    Pop $HashBuffer
  ${EndIf}
  ${If} $HashDigest == 0
  ${OrIf} $HashDigest == ""
    System::Alloc 32
    Pop $HashDigest
  ${EndIf}
  ${If} $HashBuffer == 0
  ${OrIf} $HashDigest == 0
    Goto hash_cleanup
  ${EndIf}
  hash_read:
    System::Call 'kernel32::ReadFile(p $HashFileHandle, p $HashBuffer, i 1048576, *i.r2, p 0) i.r1'
    ${If} $1 == 0
      Goto hash_cleanup
    ${EndIf}
    StrCpy $HashRead $2
    ${If} $HashRead == 0
      Goto hash_finish
    ${EndIf}
    System::Call 'bcrypt::BCryptHashData(p $HashContext, p $HashBuffer, i $HashRead, i 0) i.r1'
    ${If} $1 != 0
      Goto hash_cleanup
    ${EndIf}
    Goto hash_read
  hash_finish:
    System::Call 'bcrypt::BCryptFinishHash(p $HashContext, p $HashDigest, i 32, i 0) i.r1'
    ${If} $1 != 0
      Goto hash_cleanup
    ${EndIf}
    System::Call 'crypt32::CryptBinaryToStringW(p $HashDigest, i 32, i 0x4000000c, w.r0, *i ${NSIS_MAX_STRLEN}) i.r1'
    ${If} $1 != 0
      StrCpy $HashValue $0
    ${EndIf}
  hash_cleanup:
    ${If} $HashValue == ""
      ; A failed partial hash must never contaminate the next verification or rollback.
      ${If} $HashContext != 0
      ${AndIf} $HashContext != ""
        System::Call 'bcrypt::BCryptDestroyHash(p $HashContext)'
      ${EndIf}
      StrCpy $HashContext 0
    ${EndIf}
    ${If} $HashFileHandle != -1
      System::Call 'kernel32::CloseHandle(p $HashFileHandle)'
      StrCpy $HashFileHandle -1
    ${EndIf}
    Push $HashValue
FunctionEnd
Function ${PREFIX}CloseHashProvider
  ${If} $HashContext != 0
  ${AndIf} $HashContext != ""
    System::Call 'bcrypt::BCryptDestroyHash(p $HashContext)'
  ${EndIf}
  StrCpy $HashContext 0
  ${If} $HashDigest != 0
  ${AndIf} $HashDigest != ""
    System::Free $HashDigest
  ${EndIf}
  StrCpy $HashDigest 0
  ${If} $HashBuffer != 0
  ${AndIf} $HashBuffer != ""
    System::Free $HashBuffer
  ${EndIf}
  StrCpy $HashBuffer 0
  ${If} $HashAlgorithm != 0
  ${AndIf} $HashAlgorithm != ""
    System::Call 'bcrypt::BCryptCloseAlgorithmProvider(p $HashAlgorithm, i 0)'
  ${EndIf}
  StrCpy $HashAlgorithm 0
FunctionEnd
!macroend
!insertmacro HashFunction ""
!insertmacro HashFunction "un."

Function ValidateInventoryText
  ; In: stack text. Out: 1 only for exactly 64 lowercase hexadecimal characters.
  Pop $R0
  StrLen $R1 $R0
  ${If} $R1 != 64
    Push 0
    Return
  ${EndIf}
  StrCpy $R1 0
  validate_hex:
    StrCpy $R2 $R0 1 $R1
    StrCpy $R3 "0123456789abcdef"
    StrCpy $R4 0
    validate_character:
      StrCpy $R5 $R3 1 $R4
      ${If} $R5 == ""
        Push 0
        Return
      ${EndIf}
      ${If} $R5 == $R2
        Goto valid_character
      ${EndIf}
      IntOp $R4 $R4 + 1
      Goto validate_character
    valid_character:
      IntOp $R1 $R1 + 1
      ${If} $R1 < 64
        Goto validate_hex
      ${EndIf}
  Push 1
FunctionEnd

Function ValidateDecimalText
  Pop $R0
  StrLen $R1 $R0
  ${If} $R1 < 1
  ${OrIf} $R1 > 19
    Push 0
    Return
  ${EndIf}
  StrCpy $R1 0
  decimal_loop:
    StrCpy $R2 $R0 1 $R1
    ${If} $R2 == ""
      Push 1
      Return
    ${EndIf}
    StrCpy $R3 "0123456789"
    StrCpy $R4 0
    decimal_character:
      StrCpy $R5 $R3 1 $R4
      ${If} $R5 == ""
        Push 0
        Return
      ${EndIf}
      ${If} $R5 == $R2
        IntOp $R1 $R1 + 1
        Goto decimal_loop
      ${EndIf}
      IntOp $R4 $R4 + 1
      Goto decimal_character
FunctionEnd

Function ParseUpdateArguments
  StrCpy $AutomaticUpdate 0
  StrCpy $Guard 0
  StrCpy $SessionGuard 0
  StrCpy $UpdateInProgress 0
  StrCpy $RollbackInProgress 0
  StrCpy $UpdateParentCompleted 0
  StrCpy $UpdateRestartAttempted 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/AUTOUPDATE=" $R1
  ${If} ${Errors}
    Return
  ${EndIf}
  ${If} $R1 != "1"
    Call Fail
  ${EndIf}
  StrCpy $AutomaticUpdate 1
  ${GetOptions} $R0 "/UPDATEPID=" $UpdateParentPid
  ${GetOptions} $R0 "/UPDATESTART=" $UpdateParentStart
  ${GetOptions} $R0 "/PREVIOUSINVENTORY=" $PreviousInventory
  Push $PreviousInventory
  Call ValidateInventoryText
  Pop $R1
  ${If} $R1 != 1
    Call Fail
  ${EndIf}
  Push $UpdateParentPid
  Call ValidateDecimalText
  Pop $R1
  ${If} $R1 != 1
    Call Fail
  ${EndIf}
  IntFmt $R1 "%u" $UpdateParentPid
  ${If} $R1 != $UpdateParentPid
  ${OrIf} $UpdateParentPid <= 0
    Call Fail
  ${EndIf}
  Push $UpdateParentStart
  Call ValidateDecimalText
  Pop $R1
  ${If} $R1 != 1
    Call Fail
  ${EndIf}
FunctionEnd

Function WaitForUpdateParent
  ${If} $AutomaticUpdate != 1
    Return
  ${EndIf}
  ; SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION; never terminate the process.
  System::Call 'kernel32::OpenProcess(i 0x00101000, i 0, i $UpdateParentPid) p.r0 ?e'
  Pop $1
  StrCpy $UpdateParentHandle $0
  ${If} $UpdateParentHandle == 0
    ; ERROR_INVALID_PARAMETER means the parent completed before setup opened it.
    ; The ordinary guarded process scan still proves Citrus is no longer running.
    ${If} $1 == 87
      StrCpy $UpdateParentCompleted 1
      Return
    ${EndIf}
    Call Fail
  ${EndIf}
  System::Alloc 32
  Pop $UpdateTimeBuffer
  ${If} $UpdateTimeBuffer == 0
    System::Call 'kernel32::CloseHandle(p $UpdateParentHandle)'
    Call Fail
  ${EndIf}
  IntOp $0 $UpdateTimeBuffer + 8
  IntOp $1 $UpdateTimeBuffer + 16
  IntOp $2 $UpdateTimeBuffer + 24
  System::Call 'kernel32::GetProcessTimes(p $UpdateParentHandle, p $UpdateTimeBuffer, p r0, p r1, p r2) i.r3'
  System::Call '*$UpdateTimeBuffer(l.r0)'
  StrCpy $UpdateActualStart $0
  System::Free $UpdateTimeBuffer
  ${If} $3 == 0
  ${OrIf} $UpdateActualStart != $UpdateParentStart
    System::Call 'kernel32::CloseHandle(p $UpdateParentHandle)'
    Call Fail
  ${EndIf}
  !ifndef CITRUS_TEST_FIXTURE
    StrCpy $1 ${NSIS_MAX_STRLEN}
    System::Call 'kernel32::QueryFullProcessImageNameW(p $UpdateParentHandle, i 0, w .r0, *i r1) i.r2'
    System::Call 'kernel32::lstrcmpiW(w r0, w "$INSTDIR\Citrus.Windows.exe") i.r3'
    ${If} $2 == 0
    ${OrIf} $3 != 0
      System::Call 'kernel32::CloseHandle(p $UpdateParentHandle)'
      Call Fail
    ${EndIf}
  !endif
  System::Call 'kernel32::WaitForSingleObject(p $UpdateParentHandle, i 60000) i.r0'
  System::Call 'kernel32::CloseHandle(p $UpdateParentHandle)'
  ${If} $0 != 0
    Call Fail
  ${EndIf}
  StrCpy $UpdateParentCompleted 1
FunctionEnd

Function RestartPreviousResident
  ${If} $AutomaticUpdate != 1
  ${OrIf} $UpdateParentCompleted != 1
  ${OrIf} $UpdateRestartAttempted == 1
    Return
  ${EndIf}
  StrCpy $UpdateRestartAttempted 1
  ${If} $Guard == 0
    Call AcquireGuards
    Call CheckInstallPaths
  ${EndIf}
  ReadRegStr $ExistingInventory HKCU "${CITRUS_REGKEY}" "PackageInventory"
  ReadRegStr $ExistingLocation HKCU "${CITRUS_REGKEY}" "InstallLocation"
  ReadRegStr $RecoveryIdentity HKCU "${CITRUS_REGKEY}" "RecoveryIdentity"
  ${If} $ExistingInventory != $PreviousInventory
  ${OrIf} $ExistingLocation != $INSTDIR
  ${OrIf} $RecoveryIdentity != "$INSTDIR|$ExistingInventory"
    Return
  ${EndIf}
  Push "$INSTDIR\install-inventory.json"
  Call CheckPath
  Push "$INSTDIR\Citrus.Windows.exe"
  Call CheckPath
  Push "$INSTDIR\install-inventory.json"
  Call HashFile
  Pop $0
  ${If} $0 != $PreviousInventory
    Return
  ${EndIf}
  IfFileExists "$INSTDIR\Citrus.Windows.exe" 0 restart_done
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateLastError" "The update could not be installed. Your previous Citrus version is still available."
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateFailedVersion" "${CITRUS_VERSION}"
  Call FlushOwnership
  Call ReleaseGuards
  !ifdef CITRUS_TEST_FIXTURE
    WriteRegDWORD HKCU "${CITRUS_REGKEY}" "UpdateTestRestarted" 1
  !else
    Exec '$\"$INSTDIR\Citrus.Windows.exe$\" --background'
  !endif
  restart_done:
FunctionEnd

Function AbortUpgrade
  Call RollbackUpgrade
  Call RestartPreviousResident
FunctionEnd

Function ReadUpdateJournal
  ReadRegStr $UpdateState HKCU "${CITRUS_REGKEY}" "UpdateState"
  ReadRegStr $UpdateBackupPath HKCU "${CITRUS_REGKEY}" "UpdateBackupPath"
  ReadRegStr $UpdateOldInventory HKCU "${CITRUS_REGKEY}" "UpdatePreviousInventory"
  ReadRegStr $UpdateNewInventory HKCU "${CITRUS_REGKEY}" "UpdateInstalledInventory"
  ReadRegStr $UpdateOldSdk HKCU "${CITRUS_REGKEY}" "UpdatePreviousSdkLicense"
  ReadRegStr $UpdateOldVersion HKCU "${CITRUS_REGKEY}" "UpdatePreviousVersion"
FunctionEnd

Function ClearUpdateJournal
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdateState"
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdateBackupPath"
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdatePreviousInventory"
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdateInstalledInventory"
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdatePreviousSdkLicense"
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdatePreviousVersion"
  Call FlushOwnership
FunctionEnd

Function RollbackUpgrade
  ${If} $UpdateInProgress != 1
  ${OrIf} $RollbackInProgress == 1
    Return
  ${EndIf}
  StrCpy $RollbackInProgress 1
  Call ReadUpdateJournal
  Push $UpdateOldInventory
  Call ValidateInventoryText
  Pop $R0
  ${If} $R0 != 1
    Goto rollback_failed
  ${EndIf}
  Push $UpdateNewInventory
  Call ValidateInventoryText
  Pop $R0
  ${If} $R0 != 1
    Goto rollback_failed
  ${EndIf}
  ${If} $UpdateState != "Prepared"
  ${AndIf} $UpdateState != "Installing"
    Goto rollback_failed
  ${EndIf}
  ${If} $UpdateBackupPath != "$INSTDIR.previous-$UpdateOldInventory"
    Goto rollback_failed
  ${EndIf}
  Push $UpdateBackupPath
  Call CheckPath
  IfFileExists "$UpdateBackupPath\install-inventory.json" rollback_backup_exists
  Push "$INSTDIR\install-inventory.json"
  Call CheckPath
  Push "$INSTDIR\install-inventory.json"
  Call HashFile
  Pop $0
  ${If} $0 == $UpdateOldInventory
    ; A prior rollback may have restored the directory before registration finished.
    Goto rollback_restore_registration
  ${EndIf}
  Goto rollback_failed
  rollback_backup_exists:
    Push "$UpdateBackupPath\install-inventory.json"
    Call CheckPath
    Push "$UpdateBackupPath\install-inventory.json"
    Call HashFile
    Pop $0
    ${If} $0 != $UpdateOldInventory
      Goto rollback_failed
    ${EndIf}
    SetOutPath "$TEMP"
    System::Call 'kernel32::GetFileAttributesW(w "$INSTDIR") i.r0'
    ${If} $0 != -1
      ; Preserve any unrelated files that appeared in the new tree as well.
      StrCpy $UpdateFailedIndex 0
      rollback_free_slot:
        StrCpy $UpdateFailedPath "$INSTDIR.failed-$UpdateNewInventory-$UpdateFailedIndex"
        Push $UpdateFailedPath
        Call CheckPath
        System::Call 'kernel32::GetFileAttributesW(w "$UpdateFailedPath") i.r0'
        ${If} $0 != -1
          IntOp $UpdateFailedIndex $UpdateFailedIndex + 1
          ${If} $UpdateFailedIndex >= 100
            Goto rollback_failed
          ${EndIf}
          Goto rollback_free_slot
        ${EndIf}
      ClearErrors
      Rename "$INSTDIR" "$UpdateFailedPath"
      IfErrors rollback_failed
    ${EndIf}
    ClearErrors
    Rename "$UpdateBackupPath" "$INSTDIR"
    IfErrors rollback_failed
  rollback_restore_registration:
    ClearErrors
    WriteRegStr HKCU "${CITRUS_REGKEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${CITRUS_REGKEY}" "PackageInventory" "$UpdateOldInventory"
    WriteRegStr HKCU "${CITRUS_REGKEY}" "RecoveryIdentity" "$INSTDIR|$UpdateOldInventory"
    WriteRegStr HKCU "${CITRUS_REGKEY}" "AcceptedSdkLicenseSha256" "$UpdateOldSdk"
    WriteRegStr HKCU "${CITRUS_UNINSTALL_REGKEY}" "DisplayVersion" "$UpdateOldVersion"
    IfErrors rollback_failed
    Call FlushOwnership
    Call ClearUpdateJournal
    StrCpy $UpdateInProgress 0
    StrCpy $RollbackInProgress 0
    Return
  rollback_failed:
    ; Journal and all trees stay intact for a later guarded recovery attempt.
    SetErrorLevel 28
    MessageBox MB_OK|MB_ICONSTOP "Citrus could not restore the previous installation. Your files and update backup have been retained. Close Citrus and retry this installer." /SD IDOK
    Abort
FunctionEnd

Function RecoverInterruptedUpdate
  Call ReadUpdateJournal
  ${If} $UpdateState == "Prepared"
  ${OrIf} $UpdateState == "Installing"
    StrCpy $UpdateInProgress 1
    Call RollbackUpgrade
  ${ElseIf} $UpdateState != ""
  ${AndIf} $UpdateState != "Committed"
    Call Fail
  ${EndIf}
FunctionEnd

Function BeginUpgrade
  ${If} $OwnedInstallation != 2
    Return
  ${EndIf}
  Call ReadUpdateJournal
  ${If} $UpdateState != ""
    MessageBox MB_OK|MB_ICONSTOP "Start the installed Citrus version once so it can finish confirming the previous update, then retry." /SD IDOK
    Call Fail
  ${EndIf}
  StrCpy $UpdateBackupPath "$INSTDIR.previous-$ExistingInventory"
  StrCpy $UpdateOldInventory $ExistingInventory
  StrCpy $UpdateNewInventory "${INVENTORY_HASH}"
  Push $UpdateBackupPath
  Call CheckPath
  System::Call 'kernel32::GetFileAttributesW(w "$UpdateBackupPath") i.r0'
  ${If} $0 != -1
    Call Fail
  ${EndIf}
  ReadRegStr $UpdateOldSdk HKCU "${CITRUS_REGKEY}" "AcceptedSdkLicenseSha256"
  ReadRegStr $UpdateOldVersion HKCU "${CITRUS_UNINSTALL_REGKEY}" "DisplayVersion"
  ClearErrors
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateBackupPath" "$UpdateBackupPath"
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdatePreviousInventory" "$UpdateOldInventory"
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateInstalledInventory" "$UpdateNewInventory"
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdatePreviousSdkLicense" "$UpdateOldSdk"
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdatePreviousVersion" "$UpdateOldVersion"
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateState" "Prepared"
  IfErrors 0 +2
  Call Fail
  Call FlushOwnership
  StrCpy $UpdateInProgress 1
  ClearErrors
  Rename "$INSTDIR" "$UpdateBackupPath"
  IfErrors 0 +2
  Call Fail
  WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateState" "Installing"
  IfErrors 0 +2
  Call Fail
  Call FlushOwnership
FunctionEnd

!macro VerifyPackage PREFIX
  !include "${GENERATED}\verify.nsh"
!macroend

Function un.CleanupUpdateBackup
  ReadRegStr $UpdateState HKCU "${CITRUS_REGKEY}" "UpdateState"
  ${If} $UpdateState == ""
    Return
  ${EndIf}
  ${If} $UpdateState != "Committed"
    Call un.Fail
  ${EndIf}
  ; Verify the installed program and its runtime before executing cleanup code.
  !insertmacro VerifyPackage "un."
  ClearErrors
  !ifdef CITRUS_TEST_FIXTURE
    ExecWait '$\"$INSTDIR\Citrus.Windows.exe$\" --cleanup-update-backup-fixture=${CITRUS_TEST_FIXTURE}' $0
  !else
    ExecWait '$\"$INSTDIR\Citrus.Windows.exe$\" --cleanup-update-backup' $0
  !endif
  ${If} ${Errors}
  ${OrIf} $0 != 0
    Call un.Fail
  ${EndIf}
FunctionEnd

Function CommitUpgrade
  !ifdef CITRUS_TEST_CORRUPT_BEFORE_VERIFY
    !ifndef CITRUS_TEST_FIXTURE
      !error "Corruption fixture requires the isolated test namespace"
    !endif
    FileOpen $0 "$INSTDIR\owned.txt" w
    FileWrite $0 "corrupt fixture bytes"
    FileClose $0
  !endif
  !insertmacro VerifyPackage ""
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdateFailedVersion"
  DeleteRegValue HKCU "${CITRUS_REGKEY}" "UpdateLastError"
  ClearErrors
  WriteRegStr HKCU "${CITRUS_REGKEY}" "AcceptedSdkLicenseSha256" "${SDK_LICENSE_SHA256}"
  IfErrors 0 +2
  Call Fail
  ${If} $UpdateInProgress == 1
    WriteRegStr HKCU "${CITRUS_REGKEY}" "UpdateState" "Committed"
    IfErrors 0 +2
    Call Fail
    Call FlushOwnership
    StrCpy $UpdateInProgress 0
  ${EndIf}
FunctionEnd
