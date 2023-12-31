#Persistent
#NoEnv  ; Recommended for performance and compatibility with future AutoHotkey releases.
; #Warn  ; Enable warnings to assist with detecting common errors.
SendMode Input  ; Recommended for new scripts due to its superior speed and reliability.
SetWorkingDir %A_ScriptDir%  ; Ensures a consistent starting directory.

Run D:\Arcade\System roms\PC Games\PAC-MAN MUSEUM\Keyboard2Xinput\Keyboard2XinputGui.exe
Sleep, 1000  ; sleep 1 second
Run, D:\Arcade\System roms\PC Games\PAC-MAN MUSEUM\PACMuseum.exe
Sleep, 1000  ; sleep 1 second

SetTimer, ProcessCheckTimer, 3000
Return
 
ProcessCheckTimer:
Process, Exist, PACMuseum.exe
pid1 := ErrorLevel
If (!pid1)
{  
   # first method: kill k2xi
   # Process, Exist, Keyboard2XinputGui.exe
   # pid2 := ErrorLevel
   # If (pid2)
   #    Process, Close, %pid2%
   # ExitApp

   #second method: stop keyboard2Xinput by sending numpad multiply key (see k2x mapping.ini)
   SendInput {NumpadMult}
   ExitApp
}
Return