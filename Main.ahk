#Requires AutoHotkey v2.0

#Include <DefaultJSON> ; The Default Settings Values
#Include <JSON>
#Include <LiveThumb>
#Include <../src/Main_Class>
#Include <../src/ThumbWindow>
#Include <../src/TrayMenu>
#Include <../src/Propertys>
#Include <../src/Settings_Gui>
#Include <../src/SetupWizard>
#Include <../src/LogMonitor>
#Include <../src/StatTracker>
#Include <../src/StatWindow>

#SingleInstance Force
Persistent
ListLines False
KeyHistory 0

CoordMode "Mouse", "Screen" ; to track Window Mouse possition while DragMoving the thumbnails
SetWinDelay -1
FileEncoding("UTF-8") ; Encoding for JSSON file

SetTitleMatchMode 3

A_MaxHotKeysPerInterval := 10000 

/*
TODO #########################
*/

;@Ahk2Exe-Let U_version = 1.0.4.
;@Ahk2Exe-SetVersion %U_version%
;@Ahk2Exe-SetFileVersion %U_version%
;@Ahk2Exe-SetCopyright gonzo83
;@Ahk2Exe-SetDescription EVE MultiPreview
;@Ahk2Exe-SetProductName EVE MultiPreview
;@Ahk2Exe-ExeName EVE MultiPreview

;@Ahk2Exe-AddResource icon.ico, 160  ; Replaces 'H on blue'
;@Ahk2Exe-AddResource icon-suspend.ico, 206  ; Replaces 'S on green'
;@Ahk2Exe-AddResource icon.ico, 207  ; Replaces 'H on red'
;@Ahk2Exe-AddResource icon-suspend.ico, 208  ; Replaces 'S on red'

;@Ahk2Exe-SetMainIcon icon.ico

if !(A_IsCompiled)
    TraySetIcon("icon.ico",,true)

; Catch all unhandled Errors to prevent the Script from stopping 
OnError(Error_Handler)

Call := Main_Class()

; Show setup wizard on first launch
if (!Call.SetupCompleted) {
    wizard := SetupWizard(Call)
    WinWaitClose(wizard.W_Gui.Hwnd)
    wizard := ""
}



Load_JSON() {
    DJSON := JSON.Load(default_JSON)

    ; Offer migration from old EVE-X-Preview config
    if (FileExist("EVE-X-Preview.json") && !FileExist("EVE MultiPreview.json")) {
        result := MsgBox("Found settings from EVE-X-Preview.`n`nWould you like to migrate your existing settings to EVE MultiPreview?", "EVE MultiPreview — Migrate Settings", "YesNo Icon?")
        if (result = "Yes") {
            FileCopy("EVE-X-Preview.json", "EVE MultiPreview.json")
        }
    }

    if !(FileExist("EVE MultiPreview.json")) {
        FileAppend(JSON.Dump(DJSON,,"    " ), "EVE MultiPreview.json")
        _JSON :=  JSON.Load(FileRead("EVE MultiPreview.json"))
        return _JSON
    }
    else {
        Try {
            if (FileExist("EVE MultiPreview.json")) {
                ;if needed because of Backward combativity from the alpha versions 
                MergeJson()
            }
            _JSON := JsonMergeNoOverwrite(
                                            DJSON,
                                            JSON.Load(FileRead("EVE MultiPreview.json"))
                                        )
            FileDelete("EVE MultiPreview.json")   
            FileAppend(JSON.Dump(_JSON,,"    " ), "EVE MultiPreview.json")
        }
        catch as e  {
            value := MsgBox("The settings file is corrupted. Do you want to create a new one?",,"OKCancel")
            if (value = "Cancel") 
                ExitApp()

            FileDelete("EVE MultiPreview.json")
            FileAppend(JSON.Dump(DJSON,, "    "), "EVE MultiPreview.json")
            _JSON :=  JSON.Load(FileRead("EVE MultiPreview.json"))
        }
    }
    return _JSON
}

;Compare the User json wit the default Json to check if any key changed for possible future updates.
JsonMergeNoOverwrite(obj1, obj2) {
    for key, value in obj1 {
        if (obj2.Has(key)) {
            if (IsObject(value) && IsObject(obj2[key]))
                obj2[key] := JsonMergeNoOverwrite(value, obj2[key])
        } else {
            obj2[key] := value
        }
    }
    return obj2
}


;THis function is only used to merge the Json from old versions into the new one 
MergeJson(Settingsfile := "EVE MultiPreview.json", dJson := JSON.Load(default_JSON)) {    
    ;Load the content from the existing Json File
    fileObj := FileOpen(Settingsfile,"r", "Utf-8")
    JsonRaw := fileObj.Read(), fileObj.Close()
    OldJson := JSON.Load(JsonRaw)
    savetofile := 0

       
    for Profiles, settings in OldJson["_Profiles"] {
        if (Profiles = "Default") {
           continue
        }
        dJson["_Profiles"][Profiles] := Map()
        for k, v in settings { 
            if (OldJson["_Profiles"][Profiles].Has("ClientPossitions")) { 
                savetofile := 1
                if (k = "ClientPossitions")
                    dJson["_Profiles"][Profiles]["Client Possitions"] := v            
                else if (k = "ClientSettings")
                    dJson["_Profiles"][Profiles]["Client Settings"] :=  v
                else if (k = "ThumbnailSettings")
                    dJson["_Profiles"][Profiles]["Thumbnail Settings"] := v
                else if (k = "ThumbnailPositions")
                    dJson["_Profiles"][Profiles]["Thumbnail Positions"] :=  v
                else if (k = "Thumbnail_visibility")
                    dJson["_Profiles"][Profiles]["Thumbnail Visibility"] := v
                else if (k = "Custom_Colors")
                    dJson["_Profiles"][Profiles]["Custom Colors"] := dJson["_Profiles"]["Default"]["Custom Colors"]
                else if (k = "Hotkey_Groups")
                    dJson["_Profiles"][Profiles]["Hotkey Groups"] := v
                else if (k = "Hotkeys") {
                    if (Type(v) = "Map") {
                        Arr := []
                        for char, hotkey in v
                            Arr.Push(Map(char, hotkey))
                        dJson["_Profiles"][Profiles]["Hotkeys"] := Arr
                    }                
                }
            }
        }
    }
    if savetofile {
        dJson["global_Settings"] := OldJson["global_Settings"]  
        
        fileObj := FileOpen(Settingsfile,"w", "Utf-8")
        fileObj.Write(JSON.Dump(dJson,, "    ")), fileObj.Close()
    }
}

; Hanles unmanaged Errors
Error_Handler(Thrown, Mode) {
    ; Silently skip known-harmless AHK v2 GUI errors (panel init, control styling)
    errType := Type(Thrown)
    if (errType = "ValueError" && InStr(Thrown.Message, "Not supported for this control type"))
        return -1
    if (errType = "IndexError" && InStr(Thrown.Message, "Invalid index"))
        return -1
    ; Suppress harmless DWM thumbnail race condition (WinGetClientPos on destroyed windows)
    if (InStr(Thrown.Message, "Missing a required parameter"))
        return -1

    try {
        timestamp := FormatTime(, "yyyy-MM-dd HH:mm:ss")
        errMsg := Thrown.Message
        errFile := ""
        errLine := ""
        try errFile := Thrown.File
        try errLine := Thrown.Line
        logLine := "[" timestamp "] " errType ": " errMsg
        if (errFile != "")
            logLine .= " | File: " errFile
        if (errLine != "")
            logLine .= " | Line: " errLine
        logLine .= " | Mode: " Mode "`n"
        FileAppend(logLine, "error_log.txt")
    }
    return -1
}