Set WshShell = CreateObject("WScript.Shell")
strDesktop = WshShell.SpecialFolders("Desktop")
strFolder = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)

Set oShortcut = WshShell.CreateShortcut(strDesktop & "\Trade Reaper.lnk")
oShortcut.TargetPath = strFolder & "\TradeReaper.bat"
oShortcut.WorkingDirectory = strFolder
oShortcut.IconLocation = strFolder & "\logo.ico, 0"
oShortcut.Description = "Trade Reaper v3.0 - Dashboard + Bridge Server"
oShortcut.WindowStyle = 1
oShortcut.Save

MsgBox "Trade Reaper shortcut created on your Desktop!", vbInformation, "Trade Reaper"
