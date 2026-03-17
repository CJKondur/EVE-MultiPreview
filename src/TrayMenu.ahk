

Class TrayMenu extends Settings_Gui {
    TrayMenuObj := A_TrayMenu
    Saved_overTray := 0
    Tray_Profile_scwitch := 0

    TrayMenu() {
        Profiles_Submenu := Menu()

        for k in This.Profiles {
            If (k = This.LastUsedProfile) {
                Profiles_Submenu.Add(This.LastUsedProfile, MenuHandler)
                Profiles_Submenu.Check(This.LastUsedProfile)
                continue
            }
            Profiles_Submenu.Add(k, MenuHandler)
        }

        TrayMenu := This.TrayMenuObj
        TrayMenu.Delete() ; Delete the Default TrayMenu Items

        TrayMenu.Add("Open", MenuHandler)
        TrayMenu.Add() ; Seperator
        TrayMenu.Add("Profiles", Profiles_Submenu)
        TrayMenu.Add() ; Seperator
        TrayMenu.Add("Suspend Hotkeys", MenuHandler)
        TrayMenu.Add()
        TrayMenu.Add()
        TrayMenu.Add("Close all EVE Clients", (*) => This.CloseAllEVEWindows())
        TrayMenu.Add()
        TrayMenu.Add()
        TrayMenu.Add("Restore Client Positions", MenuHandler)
        if (This.TrackClientPossitions)
            TrayMenu.check("Restore Client Positions")
        else
            TrayMenu.Uncheck("Restore Client Positions")

        TrayMenu.Add("Save Client Positions", (*) => This.Client_Possitions())
        TrayMenu.Add()
        TrayMenu.Add()
        TrayMenu.Add("Save Thumbnail Positions", MenuHandler)
        TrayMenu.Add("Lock Positions", MenuHandler)
        if (This.LockPositions)
            TrayMenu.Check("Lock Positions")
        TrayMenu.Add("Hide/Show Thumbnails", MenuHandler)
        TrayMenu.Add("Hide/Show Primary", MenuHandler)
        TrayMenu.Add("Hide/Show PiP", MenuHandler)

        ; Build PiP Individual submenu from saved secondary thumbnails
        PiP_Submenu := Menu()
        This._PiP_Submenu := PiP_Submenu  ; store reference for handler
        try {
            for charName, settings in This.SecondaryThumbnails {
                PiP_Submenu.Add(charName, ObjBindMethod(This, "_TrayPiPToggle"))
                isEnabled := settings.Has("enabled") ? settings["enabled"] : true
                if (isEnabled)
                    PiP_Submenu.Check(charName)
            }
        }
        TrayMenu.Add("PiP Individual", PiP_Submenu)

        TrayMenu.Add("Reload", (*) => Reload())
        TrayMenu.Add()
        TrayMenu.Add("Exit", (*) => ExitApp())
        TrayMenu.Default := "Open"

        MenuHandler(ItemName, ItemPos, MyMenu) {
            If (ItemName = "Exit")
                ExitApp
            Else if (ItemName = "Save Thumbnail Positions") {
                ; Saved Thumbnail Positions only if the Saved button is used on the Traymenu
                This.Save_ThumbnailPossitions
            }
            Else if (ItemName = "Restore Client Positions") {
                This.TrackClientPossitions := !This.TrackClientPossitions
                TrayMenu.ToggleCheck("Restore Client Positions")
                SetTimer(This.Save_Settings_Delay_Timer, -200)
            }
            Else if (ItemName = "Lock Positions") {
                This.LockPositions := !This.LockPositions
                TrayMenu.ToggleCheck("Lock Positions")
                SetTimer(This.Save_Settings_Delay_Timer, -200)
                ToolTip(This.LockPositions ? "Positions Locked" : "Positions Unlocked", , , 7)
                SetTimer () => ToolTip(, , , 7), -1500
            }
            Else if (ItemName = "Hide/Show Thumbnails") {
                This.ToggleThumbnailVisibility()
                TrayMenu.ToggleCheck("Hide/Show Thumbnails")
            }
            Else if (ItemName = "Hide/Show Primary") {
                This.TogglePrimaryVisibility()
                TrayMenu.ToggleCheck("Hide/Show Primary")
            }
            Else if (ItemName = "Hide/Show PiP") {
                This.ToggleSecondaryVisibility()
                TrayMenu.ToggleCheck("Hide/Show PiP")
            }
            Else if (This.Profiles.Has(ItemName)) {
                ; Change the lastUsedProfile to the Profile name, save it to Json file and reload the script with the new Settings
                This.LastUsedProfile := ItemName
                This.SaveJsonToFile()
                Sleep(500)
                Reload()
            }
            Else if (ItemName = "Open") {
                if WinExist("EVE MultiPreview - Settings") {
                    WinActivate("EVE MultiPreview - Settings")
                    Return
                }
                This.MainGui()
            }
            Else If (ItemName = "Suspend Hotkeys") {
                Suspend(-1)
                TrayMenu.ToggleCheck("Suspend Hotkeys")
            }

        }
    }

    ; Toggle individual PiP character from tray submenu
    _TrayPiPToggle(charName, *) {
        if (!This.SecondaryThumbnails.Has(charName))
            return

        settings := This.SecondaryThumbnails[charName]
        isEnabled := settings.Has("enabled") ? settings["enabled"] : true
        newState := !isEnabled
        settings["enabled"] := newState
        This.SecondaryThumbnails[charName] := settings

        ; Update tray checkmark
        if (newState)
            This._PiP_Submenu.Check(charName)
        else
            This._PiP_Submenu.Uncheck(charName)

        ; Hide/show the live secondary thumbnail if it exists
        for eveHwnd in This.SecondaryThumbWindows.OwnProps() {
            try {
                secGui := This.SecondaryThumbWindows.%eveHwnd%["Window"]
                if (InStr(secGui.Title, "SEC_" charName)) {
                    if (newState) {
                        secGui.Show("NoActivate")
                        if (This.SecondaryThumbWindows.%eveHwnd%.Has("TextOverlay"))
                            This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Show("NoActivate")
                    } else {
                        secGui.Hide()
                        if (This.SecondaryThumbWindows.%eveHwnd%.Has("TextOverlay"))
                            This.SecondaryThumbWindows.%eveHwnd%["TextOverlay"].Hide()
                    }
                    break
                }
            }
        }

        SetTimer(This.Save_Settings_Delay_Timer, -200)
        ToolTip("PiP " charName ": " (newState ? "Enabled" : "Disabled"), , , 5)
        SetTimer () => ToolTip(, , , 5), -1500
    }

    CloseAllEVEWindows(*) {
        try {
            list := WinGetList("Ahk_Exe exefile.exe")
            GroupAdd("EVE", "Ahk_Exe exefile.exe")
            for k in list {
                PostMessage 0x0112, 0xF060, , , k
                Sleep(50)
            }
        }
    }
}
