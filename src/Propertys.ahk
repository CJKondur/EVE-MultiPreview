

class Propertys extends TrayMenu {


    ;######################
    ;## Script Propertys

    SetThumbnailText[hwnd, *] {
        set {
            if (This.ThumbWindows.HasProp(hwnd)) {
                ;RegExReplace(Value, "(EVE)(?: - )?", "")
                newtext := Value

                for k, v in This.ThumbWindows.%hwnd% {
                    if (k = "Thumbnail" || k = "Border")
                        continue
                    if (k = "TextOverlay") {
                        for chwnd, cobj in v {
                            cobj.Value := newtext
                            ;ControlSetText "New Text Here", cobj
                        }
                    }
                    if (k = "Window")
                        v.Title := newtext
                }
            }
        }
    }

    Profiles => This._JSON["_Profiles"]


    ;######################
    ;## global Settings
    ThumbnailStartLocation[key] {
        get => This._JSON["global_Settings"]["ThumbnailStartLocation"][key]
        set => This._JSON["global_Settings"]["ThumbnailStartLocation"][key] := value


    }

    Minimizeclients_Delay {
        get => This._JSON["global_Settings"]["Minimize_Delay"]
        set => This._JSON["global_Settings"]["Minimize_Delay"] := (value < 50 ? "50" : value)
    }

    Suspend_Hotkeys_Hotkey {
        get => This._JSON["global_Settings"]["Suspend_Hotkeys_Hotkey"]
        set => This._JSON["global_Settings"]["Suspend_Hotkeys_Hotkey"] := value
    }

    ThumbnailBackgroundColor {
        get => convertToHex(This._JSON["global_Settings"]["ThumbnailBackgroundColor"])
        set => This._JSON["global_Settings"]["ThumbnailBackgroundColor"] := convertToHex(value)
    }

    ThumbnailSnap[*] {
        get => This._JSON["global_Settings"]["ThumbnailSnap"]
        set => This._JSON["global_Settings"]["ThumbnailSnap"] := Value
    }

    Global_Hotkeys {
        get => This._JSON["global_Settings"]["Global_Hotkeys"]
        set => This._JSON["global_Settings"]["Global_Hotkeys"] := value
    }

    ThumbnailSnap_Distance {
        get => This._JSON["global_Settings"]["ThumbnailSnap_Distance"]
        set => This._JSON["global_Settings"]["ThumbnailSnap_Distance"] := (value ? value : "20")
    }


    ThumbnailMinimumSize[key] {
        get => This._JSON["global_Settings"]["ThumbnailMinimumSize"][key]
        set => This._JSON["global_Settings"]["ThumbnailMinimumSize"][key] := value
    }

    CharSelect_ForwardHotkey {
        get => This._JSON["global_Settings"]["CharSelect_ForwardHotkey"]
        set => This._JSON["global_Settings"]["CharSelect_ForwardHotkey"] := value
    }
    CharSelect_BackwardHotkey {
        get => This._JSON["global_Settings"]["CharSelect_BackwardHotkey"]
        set => This._JSON["global_Settings"]["CharSelect_BackwardHotkey"] := value
    }
    CharSelect_CyclingEnabled {
        get => This._JSON["global_Settings"]["CharSelect_CyclingEnabled"]
        set => This._JSON["global_Settings"]["CharSelect_CyclingEnabled"] := value
    }
    EnableKeyBlockGuard {
        get {
            try return This._JSON["global_Settings"]["EnableKeyBlockGuard"]
            catch {
                This._JSON["global_Settings"]["EnableKeyBlockGuard"] := true
                return true
            }
        }
        set => This._JSON["global_Settings"]["EnableKeyBlockGuard"] := value
    }
    RTSS_Enabled {
        get => This._JSON["global_Settings"]["RTSS_Enabled"]
        set => This._JSON["global_Settings"]["RTSS_Enabled"] := value
    }
    RTSS_IdleFPS {
        get => This._JSON["global_Settings"]["RTSS_IdleFPS"]
        set => This._JSON["global_Settings"]["RTSS_IdleFPS"] := value
    }
    SimpleMode {
        get => This._JSON["global_Settings"]["SimpleMode"]
        set => This._JSON["global_Settings"]["SimpleMode"] := value
    }
    SetupCompleted {
        get => This._JSON["global_Settings"]["SetupCompleted"]
        set => This._JSON["global_Settings"]["SetupCompleted"] := value
    }
    ClickThroughHotkey {
        get => This._JSON["global_Settings"]["ClickThroughHotkey"]
        set => This._JSON["global_Settings"]["ClickThroughHotkey"] := value
    }
    ShowSessionTimer {
        get => This._JSON["global_Settings"]["ShowSessionTimer"]
        set => This._JSON["global_Settings"]["ShowSessionTimer"] := value
    }
    ShowSystemName {
        get => This._JSON["global_Settings"]["ShowSystemName"]
        set => This._JSON["global_Settings"]["ShowSystemName"] := value
    }
    PreferredMonitor {
        get => This._JSON["global_Settings"]["PreferredMonitor"]
        set => This._JSON["global_Settings"]["PreferredMonitor"] := value
    }
    ThumbnailGroups {
        get => This._JSON["global_Settings"]["ThumbnailGroups"]
        set => This._JSON["global_Settings"]["ThumbnailGroups"] := value
    }
    EnableAttackAlerts {
        get => This._JSON["global_Settings"]["EnableAttackAlerts"]
        set => This._JSON["global_Settings"]["EnableAttackAlerts"] := value
    }

    ; Look up a character's group border color (returns "" if not in any group)
    GetGroupColor(charName) {
        for idx, group in This.ThumbnailGroups {
            for cidx, char in group["Characters"] {
                if (char = charName)
                    return StrReplace(StrReplace(group["Color"], "#", ""), "0x", "")
            }
        }
        return ""
    }

    SettingsWindowWidth {
        get {
            if (!This._JSON["global_Settings"].Has("SettingsWindowWidth"))
                This._JSON["global_Settings"]["SettingsWindowWidth"] := 750
            return This._JSON["global_Settings"]["SettingsWindowWidth"]
        }
        set => This._JSON["global_Settings"]["SettingsWindowWidth"] := value
    }
    SettingsWindowHeight {
        get {
            if (!This._JSON["global_Settings"].Has("SettingsWindowHeight"))
                This._JSON["global_Settings"]["SettingsWindowHeight"] := 580
            return This._JSON["global_Settings"]["SettingsWindowHeight"]
        }
        set => This._JSON["global_Settings"]["SettingsWindowHeight"] := value
    }


    ;########################
    ;## Profile ThumbnailSettings

    ShowAllColoredBorders {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowAllColoredBorders"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowAllColoredBorders"] := value
    }

    LastUsedProfile {
        get => This._JSON["global_Settings"]["LastUsedProfile"]
        set => This._JSON["global_Settings"]["LastUsedProfile"] := value
    }

    _ProfileProps {
        get {
            Arr := []
            for k in This._JSON["_Profiles"][This.LastUsedProfile] {
                If (k = "Thumbnail Positions" || k = "Client Possitions")
                    continue
                Arr.Push(k)
            }
            return Arr
        }
    }

    Thumbnail_visibility[key?] {
        get {
            return This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Visibility"]

            ; if IsSet(Key) {
            ;     Arr := Array()
            ;     for k, v in This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail_visibility"]
            ;         Arr.Push(k)
            ; return Arr
            ; }
            ; else
            ;     return This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail_visibility"]
        }
        set {
            if (IsObject(value)) {
                This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Visibility"] := value
                ;     for k, v in Value {
                ;         This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail_visibility"][k] := v
                ;     }
            }
            This.Save_Settings()

        }
    }


    HideThumbnailsOnLostFocus {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["HideThumbnailsOnLostFocus"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["HideThumbnailsOnLostFocus"] := value
    }
    ShowThumbnailsAlwaysOnTop {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowThumbnailsAlwaysOnTop"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowThumbnailsAlwaysOnTop"] := value
    }

    ThumbnailOpacity {
        get {
            percentage := This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailOpacity"]
            return Round((percentage < 0 ? 0 : percentage > 100 ? 100 : percentage) * 2.55)
        }
        set {
            This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailOpacity"] := Value
        }
    }

    ClientHighligtBorderthickness {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ClientHighligtBorderthickness"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ClientHighligtBorderthickness"] := (Trim(value, "`n ") <= 0 ? 1 : Trim(value, "`n "))
    }

    ClientHighligtColor {
        get => convertToHex(This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ClientHighligtColor"])
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ClientHighligtColor"] := convertToHex(Trim(value, "`n "))
    }
    ShowClientHighlightBorder {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowClientHighlightBorder"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowClientHighlightBorder"] := value
    }
    ThumbnailTextFont {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextFont"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextFont"] := Trim(value, "`n ")
    }
    ThumbnailTextSize {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextSize"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextSize"] := Trim(value, "`n ")
    }

    ThumbnailTextColor {
        get => convertToHex(This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextColor"])
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextColor"] := convertToHex(Trim(value, "`n "))
    }
    ShowThumbnailTextOverlay {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowThumbnailTextOverlay"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ShowThumbnailTextOverlay"] := value
    }
    ThumbnailTextMargins[var] {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextMargins"][var]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["ThumbnailTextMargins"][var] := Trim(value, "`n ")
    }
    InactiveClientBorderthickness {
        get {
            if ( !This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"].Has("InactiveClientBorderthickness") ) 
                This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["InactiveClientBorderthickness"] := "2"
            return This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["InactiveClientBorderthickness"]
        } 
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["InactiveClientBorderthickness"] := (Trim(value, "`n ") <= 0 ? 1 : Trim(value, "`n "))
    }
    InactiveClientBorderColor {
        get {
            if ( !This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"].Has("InactiveClientBorderColor") )
                This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["InactiveClientBorderColor"] := "#8A8A8A"

             return convertToHex(This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["InactiveClientBorderColor"])
        }
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Settings"]["InactiveClientBorderColor"] := convertToHex(Trim(value, "`n "))
    }


    ;########################
    ;## Profile ClientSettings


    CustomColorsGet[CName?] {
        get {
            name := "", nameIndex := 0, ctext := "", cBorder := "", cIABorder := ""
            for index, names in This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["CharNames"] {
                if (names = CName) {
                    nameIndex := index
                    name := names
                    break
                }
                else
                    nameIndex := index

            }
            if (nameIndex) {
                if (This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["Bordercolor"].Length >= nameIndex) {
                    cBorder := This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["Bordercolor"][nameIndex]
                }
                if (This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["TextColor"].Length >= nameIndex)
                    ctext := This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["TextColor"][nameIndex]
                if (This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"].Length >= nameIndex)
                    cIABorder := This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"][nameIndex]
            }
            return Map("Char", name, "Border", cBorder, "Text", ctext, "IABorder", cIABorder)
        }
    }


    IndexcChars => This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["CharNames"].Length
    IndexcBorder => This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["Bordercolor"].Length
    IndexcText => This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["TextColor"].Length
    IndexcIABorders => This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"].Length

    CustomColors_AllCharNames {
        get {
            names := ""
            for k, v in This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["CharNames"] {
                if (A_Index < This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["CharNames"].Length)
                    names .= k ": " v "`n"
                else
                    names .= k ": " v
            }
            return names
        }
        set {
            tempvar := []
            ListChars := StrSplit(value, "`n")
            for k, v in ListChars {
                chars := RegExReplace(This.CleanTitle(Trim(v, "`n ")), ".*:\s*", "")
                tempvar.Push(chars)
            }
            This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["CharNames"] := tempvar
        }
    }
    CustomColors_AllBColors {
        get {
            names := ""
            for k, v in This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["Bordercolor"] {
                if (A_Index < This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["Bordercolor"].Length)
                    names .= k ": " v "`n"
                else
                    names .= k ": " v
            }
            return names
        }
        set {
            tempvar := []
            ListChars := StrSplit(value, "`n")
            for k, v in ListChars {
                chars := RegExReplace(Trim(v, "`n "), ".*:\s*", "")
                tempvar.Push(convertToHex(chars))
            }
            This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["Bordercolor"] := tempvar
        }
    }
    CustomColors_AllTColors {
        get {
            names := ""
            for k, v in This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["TextColor"] {
                if (A_Index < This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["TextColor"].Length)
                    names .= k ": " v "`n"
                else
                    names .= k ": " v
            }
            return names
        }
        set {
            tempvar := []
            ListChars := StrSplit(value, "`n")
            for k, v in ListChars {
                chars := RegExReplace(Trim(v, "`n "), ".*:\s*", "")
                tempvar.Push(convertToHex(chars))
            }
            This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["TextColor"] := tempvar
        }
    }

    CustomColors_IABorder_Colors {
        get {
            names := ""
            if (!This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"].Has("IABordercolor")) {
                This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"] := ["FFFFFF"]
                SetTimer(This.Save_Settings_Delay_Timer, -200)
            }
            for k, v in This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"] {
                if (A_Index < This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"].Length)
                    names .= k ": " v "`n"
                else
                    names .= k ": " v
            }
            return names
        }
        set {
            tempvar := []
            ListChars := StrSplit(value, "`n")
            for k, v in ListChars {
                chars := RegExReplace(Trim(v, "`n "), ".*:\s*", "")
                tempvar.Push(convertToHex(chars))
            }
            This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColors"]["IABordercolor"] := tempvar
        }
    }


    CustomColorsActive {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColorActive"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Custom Colors"]["cColorActive"] := Value
    }


    MinimizeInactiveClients {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["MinimizeInactiveClients"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["MinimizeInactiveClients"] := value
    }
    AlwaysMaximize {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["AlwaysMaximize"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["AlwaysMaximize"] := value
    }
    TrackClientPossitions {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["TrackClientPossitions"]
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["TrackClientPossitions"] := value
    }
    Dont_Minimize_Clients {
        get => This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["Dont_Minimize_Clients"]
        set {
            This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["Dont_Minimize_Clients"] := []

            For index, Client in StrSplit(Value, ["`n", ","]) {
                if (Client = "")
                    continue
                This._JSON["_Profiles"][This.LastUsedProfile]["Client Settings"]["Dont_Minimize_Clients"].Push(Trim(Client, "`n "))
            }
        }
    }

    ThumbnailPositions[wTitle?] {
        get {
            if (IsSet(wTitle))
                return This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Positions"][wTitle]
            return This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Positions"]
        }
        set {
            form := ["x", "y", "width", "height"]

            if !(This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Positions"].Has(wTitle))
                This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Positions"][wTitle] := Map()

            for v in form {
                This._JSON["_Profiles"][This.LastUsedProfile]["Thumbnail Positions"][wTitle][v] := value[A_Index]
            }
            SetTimer(This.Save_Settings_Delay_Timer, -200)
        }

    }

    ClientPossitions[wTitle] {
        get {
            if (This._JSON["_Profiles"][This.LastUsedProfile]["Client Possitions"].Has(wTitle))
                return This._JSON["_Profiles"][This.LastUsedProfile]["Client Possitions"][wTitle]
            else
                return 0
        }
        set {
            form := ["x", "y", "width", "height", "IsMaximized"]
            if !(This._JSON["_Profiles"][This.LastUsedProfile]["Client Possitions"].Has(wTitle))
                This._JSON["_Profiles"][This.LastUsedProfile]["Client Possitions"][wTitle] := Map()
            for v in form {
                This._JSON["_Profiles"][This.LastUsedProfile]["Client Possitions"][wTitle][v] := value[A_Index]
            }

        }
    }

    ;########################
    ;## Profile Hotkeys
    Hotkey_Groups[key?] {
        get {
            if (IsSet(key)) {
                return This._JSON["_Profiles"][This.LastUsedProfile]["Hotkey Groups"][key]
            }
            else
                return This._JSON["_Profiles"][This.LastUsedProfile]["Hotkey Groups"]
        }
        set {
            This._JSON["_Profiles"][This.LastUsedProfile]["Hotkey Groups"][Key] := Map("Characters", value, "ForwardsHotkey", "", "BackwardsHotkey", "")
        }
    }
    ; Hotkey_Groups_Hotkeys[Name?, Hotkey?] {
    ;     get {

    ;     }
    ;     set {
    ;         This._JSON["_Profiles"][This.LastUsedProfile]["Hotkey_Groups"][Name][Hotkey] := Value
    ;     }
    ; }


    _Hotkeys[key?] {
        get {
            if (IsSet(Key)) {
                loop This._JSON["_Profiles"][This.LastUsedProfile]["Hotkeys"].Length {
                    if (This._JSON["_Profiles"][This.LastUsedProfile]["Hotkeys"][A_Index].Has(key)) {
                        return This._JSON["_Profiles"][This.LastUsedProfile]["Hotkeys"][A_Index][key]
                    }
                }
                return 0
            }
            if !(IsSet(Key))
                return This._JSON["_Profiles"][This.LastUsedProfile]["Hotkeys"]
        }
        set => This._JSON["_Profiles"][This.LastUsedProfile]["Hotkeys"] := Value
    }

    _Hotkey_Delete(*) {
        if (This.LV_Item) {
            try {
                HKey_Char_Name := This.LV.GetText(This.LV_Item)
                if (This._Hotkeys.Has(HKey_Char_Name)) {
                    This._Hotkeys.Delete(HKey_Char_Name)
                    This.LV.Delete(This.LV_Item)

                    ;This.Save_Settings()
                }
            }
        }
    }

    _Hotkey_Add(*) {
        Obj := InputBox("Enter the Char Name", "Add New Char", "w200 h90")
        if (Obj.Result = "OK") {
            This._Hotkeys[Trim(Obj.Value, " ")] := ""
            This.LV.Add(, Trim(Obj.Value, " "))

            ;This.Save_Settings()
        }
    }

    _Hotkey_Edit(*) {
        if (This.LV_Item) {
            HKey_Char_Key := This.LV.GetText(This.LV_Item, 2), HKey_Char_Name := This.LV.GetText(This.LV_Item)
            if (This._Hotkeys.Has(HKey_Char_Name)) {
                Obj := InputBox(HKey_Char_Key, "Edit Hotkey for -> " HKey_Char_Name, "w250 h100")
                if (Obj.Result = "OK") {
                    This._Hotkeys[HKey_Char_Name] := Trim(Obj.Value, " ")
                    This.LV.Modify(This.LV_Item, , , Trim(Obj.Value, " "))
                    This.LV.Modify(This.LV_Item, "+Focus +Select")

                    ;This.Save_Settings()
                }
            }
        }
    }


    _Tv_LVSelectedRow(GuiCtrlObj, Item, Checked) {
        Obj := Map()
        if (GuiCtrlObj == This.Tv_LV) {
            loop {
                RowNumber := This.Tv_LV.GetNext(A_Index - 1, "Checked")
                if not RowNumber  ; The above returned zero, so there are no more selected rows.
                    break

                Obj[This.Tv_LV.GetText(RowNumber)] := 1
                This.Thumbnail_visibility[This.Tv_LV.GetText(RowNumber)] := 1
                ;MsgBox(GuiCtrlObj.value)
            }
            This.Thumbnail_visibility := Obj
            SetTimer(This.Save_Settings_Delay_Timer, -200)
            This.NeedRestart := 1
            ;This.LV_Item := Item
            ; ddd := GuiCtrlObj.GetText(Item)
            ; ToolTip(Item ", " ddd " -, " Checked)
        }
    }


    _LVSelectedRow(GuiCtrlObj, Item, Selected) {
        if (GuiCtrlObj == This.LV && Selected) {
            This.LV_Item := Item
            ddd := GuiCtrlObj.GetText(Item)
            ;ToolTip(Item ", " ddd " -, " Selected)
        }
    }


    ;######################
    ;## Methods


    Suspend_Hotkeys(*) {
        static state := 0
        ToolTip()
        state := !state
        state ? ToolTip("Hotkeys disabled") : ToolTip("Hotkeys enabled")
        Suspend(-1)

        SetTimer((*) => ToolTip(), -1500)
    }

    Delete_Profile(*) {
        if (This.SelectProfile_DDL.Text = "Default") {
            MsgBox("You cannot delete the default settings")
            Return
        }

        if (This.SelectProfile_DDL.Text = This.LastUsedProfile) {
            This.LastUsedProfile := "Default"
        }

        This._JSON["_Profiles"].Delete(This.SelectProfile_DDL.Text)

        if (This.LastUsedProfile = "" || !This.Profiles.Has(This.LastUsedProfile))
            This.LastUsedProfile := "Default"

        ; FileDelete("EVE MultiPreview.json")
        ; FileAppend(JSON.Dump(This._JSON, , "    "), "EVE MultiPreview.json")
        SetTimer(This.Save_Settings_Delay_Timer, -200)

        ;Index := This.SelectProfile_DDL.Value
        This.SelectProfile_DDL.Delete(This.SelectProfile_DDL.Value)
        This.SelectProfile_DDL.Redraw()

        for k, v in This.S_Gui.Controls.Profile_Settings.PsDDL {
            for _, ob in v {
                ob.Enabled := 0
            }
        }

        ;This.S_Gui.Show("AutoSize")
    }


    Create_Profile(*) {
        Obj := InputBox("Enter a Profile Name", "Create New Profile", "w200 h90")
        if (Obj.Result != "OK" || Obj.Result = "")
            return
        if (This.Profiles.Has(Obj.value)) {
            MsgBox("A profile with this name already exists")
            return
        }
        if !(This.LastUsedProfile = "Default") {
            Result := MsgBox("Do you want to use the current settings for the new profile?", , "YesNo")
        }
        else
            Result := "No"

        if Result = "Yes"
            This._JSON["_Profiles"][Obj.value] := JSON.Load(FileRead("EVE MultiPreview.json"))["_Profiles"][This.LastUsedProfile]
        else if Result = "No"
            This._JSON["_Profiles"][Obj.value] := This.default_JSON["_Profiles"]["Default"]
        else
            Return 0

        FileDelete("EVE MultiPreview.json")
        FileAppend(JSON.Dump(This._JSON, , "    "), "EVE MultiPreview.json")
        This.SelectProfile_DDL.Delete()
        This.SelectProfile_DDL.Add(This.Profiles_to_Array())
        ControlChooseString(Obj.value, This.SelectProfile_DDL, "EVE MultiPreview - Settings")
        This.LastUsedProfile := Obj.value
        Return
    }
    Save_ThumbnailPossitions() {
        charSelectIdx := 0
        for EvEHwnd, GuiObj in This.ThumbWindows.OwnProps() {
            for Names, Obj in GuiObj {
                if (Names = "Window") {
                    WinGetPos(&wX, &wY, &wWidth, &wHeight, Obj.Hwnd)
                    if (Obj.Title = "" || Obj.Title = "EVE") {
                        charSelectIdx += 1
                        This.ThumbnailPositions["_CharSelect_" charSelectIdx] := [wX, wY, wWidth, wHeight]
                    } else {
                        This.ThumbnailPositions[Obj.Title] := [wX, wY, wWidth, wHeight]
                    }
                }
            }
        }
    }

    ;### Stores the Thumbnail Size and Possitions in the Json file
    Save_Settings() {
        charSelectIdx := 0
        for EvEHwnd, GuiObj in This.ThumbWindows.OwnProps() {
            for Names, Obj in GuiObj {
                if (Names = "Window") {
                    WinGetPos(&wX, &wY, &wWidth, &wHeight, Obj.Hwnd)
                    if (Obj.Title = "" || Obj.Title = "EVE") {
                        charSelectIdx += 1
                        This.ThumbnailPositions["_CharSelect_" charSelectIdx] := [wX, wY, wWidth, wHeight]
                    } else {
                        This.ThumbnailPositions[Obj.Title] := [wX, wY, wWidth, wHeight]
                    }
                }
            }
        }
        SetTimer(This.Save_Settings_Delay_Timer, -200)
    }
}


;########################
;## Functions

Add_New_Profile() {
    return
}

convertToHex(rgbString) {
    ; Check if the string corresponds to the decimal value format (e.g. "255, 255, 255" or "rgb(255, 255, 255)")
    if (RegExMatch(rgbString, "^\s*(rgb\s*\(?)?\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)?\s*$", &matches)) {
        red := matches[2], green := matches[3], blue := matches[4]

        ; covert decimal to hex
        hexValue := Format("{:02X}{:02X}{:02X}", red, green, blue)
        return hexValue
    }

    ; Check whether the string corresponds to the hexadecimal value format (e.g "#FFFFFF" or "0xFFFFFF")
    if (RegExMatch(rgbString, "^\s*(#|0x)?([0-9A-Fa-f]{6})\s*$", &matches)) {
        hexValue := matches[2]
        hexValue := StrLower(hexValue)
        return hexValue
    }
    ;  If no match was found or the string is already in hexadecimal value format, return directly
    return rgbString
}
