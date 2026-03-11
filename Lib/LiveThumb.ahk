; ----------------------------------------------------------------------------------------------------------------------
; Name .........: LiveThumb class library
; Description ..: Windows live thumbnails implementation (requires AERO to be enabled).
; AHK Version ..: AHK v2 2.0.3 
; Author .......: cyruz - http://ciroprincipe.info
; v2 Convertion : Gonz0
; Forum  .......: Ahk V1: https://www.autohotkey.com/boards/viewtopic.php?t=66585
; Thanks .......: maul-esel - https://github.com/maul-esel/AeroThumbnail
; License ......: WTFPL - http://www.wtfpl.net/txt/copying/
; Changelog ....: Feb. 21, 2019 - v0.1.0 - First version.
; ..............: Mar. 04, 2019 - v0.1.1 - Added active properties management and propertes getters.
; ..............: Jul. 28, 2019 - v0.1.2 - Added "Discard" method. Fixed LoadLibrary return type and a wrong behavior
; ..............:                          when returning false from the constructor (thanks Helgef).
; ..............: Sep. 21, 2019 - v0.1.3 - Fixed potential issue with HRESULT return code. Used internal memory
; ..............:                          management instead of LocalAlloc.
; ..............: Sep. 21, 2019 - v0.1.4 - Fixed Object.SetCapacity not zero-filling allocated memory.
; ..............: Nov. 15, 2022 - v0.1.5 - Fixed DWM_THUMB_PROPERTIES structure offsets (thanks swagfag).
; ..............: Jun. 26, 2023 - v0.1.5 - Conerverted to Autohotkey version 2
; Remarks ......: The class registers a thumbnail and waits for properties update. When all the desired properties have
; ..............: been updated, the "Update()" method should be called to submit the properties. Getting a property
; ..............: when an update has not been performed before, will result in the string "NOT UPDATED" to be returned.
; ..............: Due to some internal unknown reason, the instantiated object must be in the global namespace to work.
; Info .........: Implements the following functions and structures from Win32 API.
; ..............: "DwmRegisterThumbnail" Win32 function:
; ..............: https://docs.microsoft.com/en-us/windows/desktop/api/dwmapi/nf-dwmapi-dwmregisterthumbnail
; ..............: "SIZE" Win32 structure:
; ..............: https://docs.microsoft.com/en-us/previous-versions//dd145106(v=vs.85)
; ..............: "DwmQueryThumbnailSourceSize" Win32 function:
; ..............: https://docs.microsoft.com/en-us/windows/desktop/api/dwmapi/nf-dwmapi-dwmquerythumbnailsourcesize
; ..............: "DWM_THUMB_PROPERTIES" Win32 structure:
; ..............: https://docs.microsoft.com/en-us/windows/desktop/api/dwmapi/ns-dwmapi-_dwm_thumbnail_properties
; ..............: "DWM_TNP" Constants for the dwFlags field:
; ..............: https://docs.microsoft.com/en-us/windows/desktop/dwm/dwm-tnp-constants
; ..............: "DwmUpdateThumbnailProperties" Win32 function:
; ..............: https://docs.microsoft.com/en-us/windows/desktop/api/dwmapi/nf-dwmapi-dwmupdatethumbnailproperties
; ..............: "DwmUnregisterThumbnail" Win32 function:
; ..............: https://docs.microsoft.com/en-us/windows/desktop/api/dwmapi/nf-dwmapi-dwmunregisterthumbnail
; ----------------------------------------------------------------------------------------------------------------------



; * How to use:
; -------------

; Instantiate the object:

;     thumb := LiveThumb(HWND Source Window, HWND Destination Window) 

; Modify its properties:

;     thumb.Source := [x, y, w, h]          ;Size of the Source Window Thumbnail. Should be the same Size of the Source Window or it shows free space
;     thumb.Destination := [x, y, w, h]     ;Size of the Destination Window Thumbnail. Shrinks or streches the Thumbnail to the given size. To show the full area its should be the same size like the Destination Window
;     thumb.Visible := True                 ;True or False Set the Thumbnail Visible
;     hLT.SourceClientAreaOnly := True      ;Optional: True or False Set Show Client Area Only.
;     hLT.Opacity := 255                    ;Optional: value between 0 and 255

; Update the properties:

;     thumb.Update()                        ;Updates the given Propertys. This should be called everytime if some value is changed.

; Get a property:

;     aProp := thumb.Visible                ; Shows 1 or 0


; * Example:
; ----------

; #SingleInstance Force
; #Include <LiveThumb>

; Run("notepad", , , &PID := 0)
; WinWait("ahk_pid " PID)
; hwndNotepad := WinExist("ahk_pid " PID)

; WinGetPos( &X, &Y, &W, &H, "ahk_pid " PID)

; Mygui := Gui()

; hLT := LiveThumb(hwndNotepad, Mygui.Hwnd)
; hLT.Source      := [0, 0, W, H]
; hLT.Destination := [0, 0, W, H]
; hLT.SourceClientAreaOnly := False
; hLT.Visible     := True
; hLT.Opacity := 255
; hLT.Update()

; Mygui.Show("w" W "h" H)


; Name .........: LiveThumb - PUBLIC CLASS
; Description ..: Manages thumbnails creation and destruction.
Class LiveThumb
{
    Static  Dll_Module :=   0,
    OBJ_COUNTER :=          0,
    DTP_SIZE :=             48,
    DTP_DWFLAGS := {    Destination:            0x0001,
                        Source:                 0x0002,
                        Opacity:                0x0004,
                        Visible:                0x0008,
                        SourceClientAreaOnly:   0x0010  },
    DTP_OFFSETS := {    dwFlags:                0,
                        Destination:            4,
                        Source:                 20,
                        Opacity:                36,
                        Visible:                37,
                        SourceClientAreaOnly:   41      }
         
          
    ; Name .........: __New - PRIVATE CONSTRUCTOR
    ; Description ..: Initialize the object, registering a new thumbnail and allocating the memory.
    ; Parameters ...: hSource = Handle to the window to be previewed.
    ; ..............: hDest   = Handle to the window containing the live preview.
    ; Return .......: LiveThumb object on success - False on error.
    __New( hSource, hDest )
    {
        ; Load the library on first run.
        If ( !LiveThumb.DLL_MODULE )
            LiveThumb.DLL_MODULE := DllCall("LoadLibrary", "Str","dwmapi.dll", "Ptr")

        LiveThumb.OBJ_COUNTER += 1

        ; Register a thumbnail to get an ID.
        If ( DllCall( "dwmapi.dll\DwmRegisterThumbnail"
                    , "Ptr",  hDest
                    , "Ptr",  hSource
                    , "Ptr*", &phThumb := 0
                    , "UPtr" ) )
            Return False
                 
        this.THUMB_ID := phThumb
        this.THUMB_UPDATED := False
        this.THUMB_PENDING_UPDATE := True

        ; We define 2 portions of memory used for properties update and active properties tracking.
        this.THUMB_UPD_PROP_PTR := Buffer(LiveThumb.DTP_SIZE,0)
        this.THUMB_ACT_PROP_PTR := Buffer(LiveThumb.DTP_SIZE,0)

        ; Object.SetCapacity doesn't zero-fill the allocated memory so we call the Discard method.
        this.Discard( )        
        Return this
    }

    ; Name .........: __Delete - PRIVATE DESTRUCTOR
    ; Description ..: Unregister the thumbnail and deallocate memory.
    __Delete( )
    {
        ; Unregister thumbnail ID.
        If ( this.THUMB_ID )
            DllCall( "dwmapi.dll\DwmUnregisterThumbnail"
                   , "Ptr", this.THUMB_ID )  
            
        ; If it's last instantiated object, free the library.
        If ( LiveThumb.DLL_MODULE && !(LiveThumb.OBJ_COUNTER -= 1) )
        {
            DllCall("FreeLibrary", "Ptr",LiveThumb.DLL_MODULE)
            LiveThumb.DLL_MODULE := 0
        }
    }

    __Get(aName, Params) {
        If ( LiveThumb.HasOwnProp(aName))
            return LiveThumb.%aName%
        
    }
    ; Name .........: QuerySourceSize - PUBLIC METHOD
    ; Description ..: Return the size (width/height) of the source thumbnail.
    ; Return .......: Array with width and height values - False on error.
    QuerySourceSize( )
    {
        SIZE := Buffer(8,0)
        If ( DllCall( "dwmapi.dll\DwmQueryThumbnailSourceSize" 
                    , "Ptr", this.THUMB_ID
                    , "Ptr", SIZE
                    , "UPtr" ) ) {
            SIZE := Buffer(0)                
            Return False 
        }
        
        SourceSize := [NumGet(SIZE, 0, "Int"), NumGet(SIZE, 4, "Int")]
        SIZE := Buffer(0)
        
        Return SourceSize
    }

    ; Name .........: Update - PUBLIC METHOD
    ; Description ..: Update thumbnail properties and zero fill the dwFlags memory to be ready for next update.
    ; Return .......: True on success - False on error.
    Update( )
    {
        ; If no update is pending, return false.
        ; If ( !this.THUMB_PENDING_UPDATE )
        ;     Return False

        ; Update properties.
        If ( DllCall( "dwmapi.dll\DwmUpdateThumbnailProperties"
                    , "Ptr", this.THUMB_ID
                    , "Ptr", this.THUMB_UPD_PROP_PTR
                    , "UPtr" ) )
            Return False
        
        ; Flag as updated and copy memory so that we can use this portion to track active properties with getters.
        this.THUMB_UPDATED := True
        DllCall( "NtDll.dll\RtlCopyMemory"
               , "Ptr",  this.THUMB_ACT_PROP_PTR
               , "Ptr",  this.THUMB_UPD_PROP_PTR
               , "UInt", LiveThumb.DTP_SIZE )
        
        ; Use the "Discard" method to reset dwFlags and return.
        this.Discard( )
        Return True
    }

    ; Name .........: Discard - PUBLIC METHOD
    ; Description ..: Discard set properties, resetting dwFlags memory.
    Discard( )
    {
        ; Zero-fill dwFlags memory in the properties update memory portion.
        NumPut("UInt", 0, this.THUMB_UPD_PROP_PTR, LiveThumb.DTP_OFFSETS.dwFlags)
        this.THUMB_PENDING_UPDATE := False
    }

    ; Name .........: Destination - PUBLIC PROPERTY
    ; Description ..: The area in the destination window where the thumbnail will be rendered.  
    ; Value ........: Array with 4 client related coordinates [ left, top, right, bottom ].
    ; Remarks ......: "Destination" property is of "RECT" type (16 bytes structure) and starts from offset 4.
    Destination {
        Get {
            If (!this.THUMB_UPDATED)
                Return "NOT UPDATED" 

            arrRet := []
            Loop 4
                arrRet.Push(NumGet(this.THUMB_ACT_PROP_PTR, LiveThumb.DTP_OFFSETS.Destination * A_Index, "Int"))
            Return arrRet
        }
        Set {
            This.SetDwFlags("Destination")
            Loop 4
                NumPut("Int", value[A_Index], this.THUMB_UPD_PROP_PTR, LiveThumb.DTP_OFFSETS.Destination * A_Index)
        }
    }

    ; Name .........: Source - PUBLIC PROPERTY
    ; Description ..: The region of the source window to use as the thumbnail. Default is the entire window.    
    ; Value ........: Array with 4 client related coordinates [ left, top, right, bottom ].
    ; Remarks ......: "Source" property is of "RECT" type (16 bytes structure) and starts from offset 20.
    Source {
        Get {
            If (!this.THUMB_UPDATED)
                Return "NOT UPDATED" 

            arrRet := []
            Loop 4
                arrRet.Push(NumGet(this.THUMB_ACT_PROP_PTR, LiveThumb.DTP_OFFSETS.Source + 4*(A_Index-1), "Int"))
            Return arrRet   
        }
        Set {
            This.SetDwFlags("Source")
            Loop 4
                NumPut("Int", value[A_Index], this.THUMB_UPD_PROP_PTR, LiveThumb.DTP_OFFSETS.Source + 4*(A_Index-1))
        }
    }

    ; Name .........: Opacity - PUBLIC PROPERTY
    ; Description ..: The opacity with which to render the thumbnail. 0: transparent - 255: opaque. Default is 255.
    ; Value ........: Integer value from 0 to 255.
    ; Remarks ......: "Opacity" property is of "BYTE" type (1 byte + 3 padding) and starts from offset 36.
    Opacity {
        Get {
            If (!this.THUMB_UPDATED)
                Return "NOT UPDATED" 

            Return NumGet(this.THUMB_ACT_PROP_PTR, LiveThumb.DTP_OFFSETS.Opacity, "UChar")
        }
        Set {
            This.SetDwFlags("Opacity")
            NumPut("UChar", value, this.THUMB_UPD_PROP_PTR, LiveThumb.DTP_OFFSETS.Opacity)
        }
    }

    ; Name .........: Visible - PUBLIC PROPERTY
    ; Description ..: True to make the thumbnail visible, otherwise False. Default is False.
    ; Value ........: Boolean True/False or integer 1/0 value.
    ; Remarks ......: "Visible" property is of "BOOL" type (4 bytes) and starts from offset 40.
    Visible {
        Get {    
            If (!this.THUMB_UPDATED)
                Return "NOT UPDATED" 

            Return NumGet(this.THUMB_ACT_PROP_PTR, LiveThumb.DTP_OFFSETS.Visible, "Int")
        }
        Set {
            This.SetDwFlags("Visible")
            NumPut("Int", value, this.THUMB_UPD_PROP_PTR, LiveThumb.DTP_OFFSETS.Visible)
        }
    }

    ; Name .........: SourceClientAreaOnly - PUBLIC PROPERTY
    ; Description ..: True to use only the thumbnail source's client area, otherwise False. Default is False.
    ; Value ........: Boolean True/False or integer 1/0 value.
    ; Remarks ......: "SourceClientAreaOnly" property is of "BOOL" type (4 bytes) and starts from offset 44.
    SourceClientAreaOnly {
        Get {
            If (!this.THUMB_UPDATED)
                Return "NOT UPDATED" 

            Return NumGet(this.THUMB_ACT_PROP_PTR, LiveThumb.DTP_OFFSETS.SourceClientAreaOnly, "Int")
        }
        Set {
            This.SetDwFlags("SourceClientAreaOnly")
            NumPut("Uint", value, this.THUMB_UPD_PROP_PTR, LiveThumb.DTP_OFFSETS.SourceClientAreaOnly)
        }
    }

    ;Writes the DWFlags from the given Property into Memory.
    SetDwFlags(aName)
    {
        If (LiveThumb.DTP_DWFLAGS.HasOwnProp(aName)) {
                NumPut( "UInt", NumGet( this.THUMB_UPD_PROP_PTR, 
                        LiveThumb.DTP_OFFSETS.dwFlags, "UInt" ) | LiveThumb.DTP_DWFLAGS.%aName%,
                        this.THUMB_UPD_PROP_PTR,
                        LiveThumb.DTP_OFFSETS.dwFlags)

                this.THUMB_PENDING_UPDATE := true            
            }
    }
}

