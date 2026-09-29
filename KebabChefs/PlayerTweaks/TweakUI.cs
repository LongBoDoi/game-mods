using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlayerTweaks;

/// <summary>
/// Injected MonoBehaviour that draws the overlay and re-applies the tweaked values.
/// Values are pushed every frame because the game itself writes to these fields
/// (SetWalkingSpeed, drunk state, cutscenes) and would otherwise stomp our changes.
///
/// Note: the sliders are hand-drawn. UnityEngine.GUI.Slider is stripped out of this
/// game's IL2CPP build, so calling it throws "Method unstripping failed". GUI.Box,
/// GUI.Label, GUI.Toggle and GUI.Button all survive, so the slider is built from
/// boxes plus manual mouse handling.
/// </summary>
public class TweakUI : MonoBehaviour
{
    public TweakUI(IntPtr ptr) : base(ptr) { }

    /// <summary>
    /// The panel's tabs, in the order they are drawn.
    ///
    /// Seven where there used to be four. The panel sizes itself to its tallest tab and stops
    /// at the height of the screen, so a tab that outgrows the screen is not a cosmetic problem
    /// - the controls at the bottom are simply not reachable. "Tương tác" had collected
    /// everything about touching things, from carry limits to plate cabinets, and "Thực đơn"
    /// held the picker and today's list and the on-screen tracker at once. Both are now split
    /// along what the sections are actually about rather than by where they happened to land.
    ///
    /// Split rather than scrolled on purpose: this game's IMGUI is partly stripped - GUI.Slider
    /// and GUI.TextField both throw here - so a scroll view is not something to bet the whole
    /// panel on, and the same paging reasoning already governs the dish picker.
    /// </summary>
    private enum Tab { Player, Teleport, Interactions, Kitchen, Placing, Dishes, Today, Events }

    /// <summary>Tab captions, indexed by <see cref="Tab"/>.</summary>
    private static readonly string[] TabLabels =
    {
        "Người chơi", "Dịch chuyển", "Tương tác", "Bếp", "Đặt đồ", "Thêm món", "Hôm nay", "Sự kiện",
    };

    private const int TabsPerRow = 4;
    private const int TabRows = 2;

    private const float PanelWidth = 520f;
    private const float Pad = 10f;
    private const float Row = 24f;
    private const float TitleH = 26f;
    private const float TabH = 26f;
    // Wider than the English layout needed: Vietnamese labels run noticeably longer
    // ("Số vật phẩm trên tay" against "Items in hand"), and IMGUI clips rather than wraps.
    private const float LabelW = 175f;
    private const float FieldW = 96f;   // wide enough to type a long value into

    private Tab _tab = Tab.Player;

    private readonly List<Teleporter.Location> _locations = new();
    private readonly List<Teleporter.Location> _custom = new();
    private Teleporter.Location _pendingDelete;
    private readonly Dictionary<string, Key> _bindings = new();

    /// <summary>Sentinels put in _bindingFor when capturing a non-location key.</summary>
    private const string BoxKeySlot = " box-destroy-key";
    private const string PlateKeySlot = " plate-align-key";
    private const string SnapKeySlot = " plate-snap-toggle-key";
    private const string TakeKeySlot = " box-take-out-key";
    private const string CatKeySlot = " cat-summon-key";

    private string _bindingFor;
    private string _status;
    private Vector3 _returnPos;
    private bool _hasReturn;
    private bool _waypointsLoaded;

    // Fall watchdog: if a jump drops us into the void, put us back.
    private const float FallLimit = 15f;
    private const float FallWatchSeconds = 5f;
    private Vector3 _landedAt;
    private float _watchFallUntil;
    private bool _watchingFall;

    private FirstPersonController _fpc;
    private CharacterItemInteract _interact;

    private bool _open;
    private bool _captured;
    private bool _capturedInteract;
    private float _rescanTimer;

    private Rect _panel = new Rect(40f, 40f, PanelWidth, 300f);
    private bool _dragging;
    private Vector2 _dragOffset;


    // The game's own values, read once so "Reset" is truthful and slider ranges make sense.
    private float _dWalk, _dRun, _dCrouch, _dJump, _dGravity, _dSens, _dInteract;

    private CursorLockMode _prevLock;
    private bool _prevCursorVisible;
    private bool _prevCanRotate;
    private bool _prevCanMove;
    private bool _tookOverCursor;
    private bool _cursorSettled;
    private int _relockCount;

    // Stripped-method failures repeat every repaint; log each unique one only once.
    private readonly HashSet<string> _loggedOnce = new();

    private void Update()
    {
        // While arming a binding we swallow all input, so the panel key itself
        // (and anything else) can be assigned to a location.
        if (_bindingFor != null)
        {
            CaptureBinding();
            return;
        }

        if (_editingField != null)
        {
            // Typing a number must not also fire the panel key or a teleport hotkey -
            // the digits are bound to locations.
            if (_textFieldOk)
            {
                // The text field consumes the characters itself; we only need to know
                // when the user is finished with it.
                if (KeyDown(Key.Escape) || KeyDown(Key.Enter) || KeyDown(Key.NumpadEnter))
                    _editingField = null;
            }
            else
            {
                HandleTextEntry();
            }
        }
        else
        {
            if (TogglePressed())
                SetOpen(!_open);

            CheckTeleportHotkeys();
        }
        TickDestinationHold();
        MultiPick.Tick(_interact, _open);
        StoreRepeater.Tick(_open);
        SpiceTweaks.Tick(_interact);
        FishCutting.Tick(_interact);
        SlicerTweaks.Tick(_interact, _open, LocalClientId());
        CarryTweaks.Tick(_interact);
        BoxTweaks.Tick(_interact, _open, LocalClientId());
        PlateTweaks.Tick(_interact, _open);
        PlaceAll.Tick(_interact, _open);
        PlateStocks.Tick(_interact, _open);
        DishStatus.Tick();
        CounterTopUp.Tick();
        CatVisits.Tick(_open);

        _rescanTimer -= Time.unscaledDeltaTime;
        if (_fpc == null || _interact == null || !_capturedInteract || _rescanTimer <= 0f)
        {
            _rescanTimer = 1f;
            FindPlayer();
        }

        // Claim the cursor before CursorManager.Update() gets a chance to re-lock it.
        if (_open)
        {
            SuppressGameCursorControl();
            SuppressGameInteraction();
        }
    }

    private void LateUpdate()
    {
        // LateUpdate runs after every Update, including CursorManager's, so this is
        // where the cursor state actually sticks.
        if (_open)
        {
            SuppressGameCursorControl();
            SuppressGameInteraction();
            ApplyCursorState();
        }

        WatchForFall();

        if (!Plugin.Enabled.Value || !_captured) return;
        ApplyTweaks();
    }

    /// <summary>
    /// A teleport can still land somewhere the ground probe misjudged. Rather than let
    /// the player fall forever, snap them back to where they jumped from.
    /// </summary>
    [HideFromIl2Cpp]
    private void WatchForFall()
    {
        if (!_watchingFall || _fpc == null) return;

        if (Time.time > _watchFallUntil) { _watchingFall = false; return; }

        if (_fpc.transform.position.y < _landedAt.y - FallLimit)
        {
            _watchingFall = false;

            if (_hasReturn && Teleporter.TeleportTo(_fpc, _returnPos, out _))
                _status = "Rơi xuyên sàn - đã đưa bạn về vị trí trước đó.";
            else
                _status = "Rơi xuyên sàn và không thể khôi phục.";

            Plugin.Logger.LogWarning($"Fall detected after teleport; {_status}");
        }
    }

    /// <summary>Never leave the game's cursor manager - or the player - switched off behind us.</summary>
    private void OnDestroy()
    {
        CursorGuard.Active = false;
        RestoreGameInteraction();
    }

    // ---------------------------------------------------------------- cursor

    /// <summary>
    /// The game's CursorManager re-hides and re-locks the cursor from its own Update.
    /// Setting the cursor back every frame produced a visible flicker, because the OS
    /// cursor was being hidden and shown once per frame. Raising OverrideForThisFrame
    /// was not enough on its own, so we switch the component off outright while the
    /// panel is open and restore it on close - one write instead of a fight.
    /// </summary>
    [HideFromIl2Cpp]
    private void SuppressGameCursorControl()
    {
        if (!Plugin.FreeCursorWhenOpen.Value) return;

        // The setter patch does the real work; this is just the game's own polite
        // "hands off" flag, kept in case anything else consults it.
        CursorGuard.Active = true;
        try { CursorManager.OverrideForThisFrame = true; }
        catch (Exception e) { LogOnce($"CursorManager.OverrideForThisFrame failed: {e.Message}"); }
    }

    [HideFromIl2Cpp]
    private void RestoreGameCursorControl()
    {
        CursorGuard.Active = false;

        try { CursorManager.OverrideForThisFrame = false; }
        catch { /* nothing sensible to do if the field is gone */ }
    }

    // The exact component we switched off, kept rather than re-derived: FindPlayer
    // re-scans on a timer, and restoring "whatever _interact points at now" would leave
    // the original one disabled for good if the player object had been replaced.
    private CharacterItemInteract _suppressedInteract;
    private bool _interactWasEnabled;

    /// <summary>
    /// Stops clicks in the panel from also reaching the world.
    ///
    /// Freeing the cursor was never enough on its own: the game goes on reading the mouse,
    /// so pressing a button in the panel also picked up whatever the crosshair happened to
    /// be resting on. Disabling CharacterItemInteract is what actually stops that, and it
    /// is a lever with a guarantee behind it rather than a guess - Unity does not call
    /// Update or LateUpdate on a disabled Behaviour, and that is where the interaction
    /// input is read. Coroutines are untouched by it, so an interaction already under way
    /// is not torn in half.
    /// </summary>
    [HideFromIl2Cpp]
    private void SuppressGameInteraction()
    {
        if (!Plugin.BlockInteractionWhenOpen.Value || _interact == null) return;

        try
        {
            if (_suppressedInteract != _interact)
            {
                RestoreGameInteraction();
                _suppressedInteract = _interact;
                _interactWasEnabled = _interact.enabled;
            }

            // Re-asserted every frame rather than written once, the way the cursor guard
            // is: anything in the game that switches the component back on would
            // otherwise quietly hand the world our clicks again.
            if (_interact.enabled) _interact.enabled = false;
        }
        catch (Exception e)
        {
            LogOnce($"Interaction guard failed: {e.GetType().Name}: {e.Message}");
        }
    }

    [HideFromIl2Cpp]
    private void RestoreGameInteraction()
    {
        if (_suppressedInteract == null) return;

        var target = _suppressedInteract;
        _suppressedInteract = null;

        try { target.enabled = _interactWasEnabled; }
        catch (Exception e) { LogOnce($"Interaction guard could not restore: {e.Message}"); }
    }

    [HideFromIl2Cpp]
    private void ApplyCursorState()
    {
        if (!Plugin.FreeCursorWhenOpen.Value) return;

        // Only write on change. Re-assigning the same value every frame is what made
        // the cursor blink in the first place.
        //
        // The first pass after opening is expected to differ - the game had the cursor
        // locked. Anything after that means something else is still writing it, which
        // is worth naming in the log rather than silently fighting over.
        bool fought = false;

        if (Cursor.lockState != CursorLockMode.None) { Cursor.lockState = CursorLockMode.None; fought = true; }
        if (!Cursor.visible) { Cursor.visible = true; fought = true; }

        // With the setter patch installed this should never fire. If it does, the
        // patch did not take and the guard needs another approach.
        if (fought && _cursorSettled)
        {
            _relockCount++;
            if (_relockCount == 1 || _relockCount == 100)
                Plugin.Logger.LogWarning(
                    $"Cursor re-locked despite the guard ({_relockCount}x); " +
                    $"guard installed = {CursorGuard.Installed}.");
        }
        _cursorSettled = true;

        // Stop the camera spinning and the character walking off while we drag sliders.
        if (_fpc != null)
        {
            if (_fpc.canRotateView) _fpc.canRotateView = false;
            if (_fpc.CanMove) _fpc.CanMove = false;
        }
    }

    // ---------------------------------------------------------------- player

    private void FindPlayer()
    {
        try
        {
            var controllers = UnityEngine.Object.FindObjectsByType<FirstPersonController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (controllers == null || controllers.Length == 0) { _fpc = null; return; }

            // Only ever touch our own character. Picking "the first controller we find"
            // would happily apply speed tweaks to - or teleport - a team mate's body in
            // co-op, so an unowned controller is only acceptable when it is the only one
            // in the scene (i.e. singleplayer, where ownership may not be set at all).
            FirstPersonController owned = null;
            int candidates = 0;

            foreach (var c in controllers)
            {
                if (c == null) continue;
                candidates++;

                var pc = c.m_PlayerCharacter;
                if (pc != null && pc.IsOwner) { owned = c; break; }
            }

            if (owned == null)
            {
                // The one-candidate fallback exists for singleplayer, where ownership may not
                // be set at all. It must never fire on a client: a late joiner arrives before
                // the game has given it a character, so the only controller in the scene is
                // the host's - and taking it meant the carry limit, the speeds and the reach
                // were all applied to the host's character instead of ours. "Only one
                // controller" does not mean "it must be mine" once there is a host to belong
                // to; it can equally mean mine does not exist yet.
                if (candidates == 1 && !IsRemoteClient())
                {
                    foreach (var c in controllers)
                        if (c != null) { owned = c; break; }
                }
                else
                {
                    LogOnce($"{candidates} player controllers and none owned by us - waiting rather than guessing.");
                    _fpc = null;
                    return;
                }
            }

            // A different character than last time means everything cached from it is stale.
            if (_fpc != owned) _interact = null;
            _fpc = owned;

            if (_interact == null)
            {
                // Scoped to our own hierarchy only. A global search would pick up another
                // player's component and quietly change their reach instead of ours.
                _interact = _fpc.transform.root.GetComponentInChildren<CharacterItemInteract>(true);
                LogOnce($"CharacterItemInteract: {(_interact == null ? "NOT FOUND" : "found")}");
            }

            if (!_captured) CaptureDefaults();
            TryCaptureInteract();

            if (!_waypointsLoaded)
            {
                _custom.Clear();
                _custom.AddRange(Teleporter.LoadCustom(Plugin.Waypoints.Value));

                _bindings.Clear();
                foreach (var kv in Teleporter.LoadBindings(Plugin.HotkeyBindings.Value))
                    _bindings[kv.Key] = kv.Value;

                _waypointsLoaded = true;
                RefreshLocations();
            }
        }
        catch (Exception e)
        {
            LogOnce($"Player scan failed: {e.Message}");
        }
    }

    private void CaptureDefaults()
    {
        _dWalk = _fpc.m_WalkSpeed;
        _dRun = _fpc.m_RunSpeed;
        _dCrouch = _fpc.m_CrouchSpeed;
        _dJump = _fpc.m_JumpSpeed;
        _dGravity = _fpc.m_GravityMultiplier;
        _dSens = _fpc.sensitivityMultiplier;

        // -1 in the config file means "never touched" -> seed from the real game value.
        Seed(Plugin.WalkSpeed, _dWalk);
        Seed(Plugin.RunSpeed, _dRun);
        Seed(Plugin.CrouchSpeed, _dCrouch);
        Seed(Plugin.JumpSpeed, _dJump);
        Seed(Plugin.GravityMultiplier, _dGravity);
        Seed(Plugin.Sensitivity, _dSens);

        _captured = true;
        Plugin.Logger.LogInfo(
            $"Captured defaults: walk={_dWalk} run={_dRun} crouch={_dCrouch} " +
            $"jump={_dJump} gravity={_dGravity} sens={_dSens}");
    }

    /// <summary>
    /// Interact distance is captured separately and only once it reads non-zero.
    /// The field is still 0 for a moment after the player spawns, and seeding 0 would
    /// pin the reach to nothing and make the game unplayable.
    /// </summary>
    private void TryCaptureInteract()
    {
        if (_capturedInteract || _interact == null) return;

        float v = _interact.defaultItemInteractDistance;
        if (v <= 0f) v = _interact.itemInteractDistance;
        if (v <= 0f) return;   // not initialised yet; try again on the next scan

        _dInteract = v;
        // Treat any non-positive stored value as "unseeded". An earlier build could
        // persist a 0 here, and 0 reach is never a value the user meant to choose.
        if (Plugin.InteractDistance.Value <= 0f) Plugin.InteractDistance.Value = v;
        _capturedInteract = true;
        Plugin.Logger.LogInfo($"Captured interact distance: {v}");
    }

    private static void Seed(ConfigEntry<float> entry, float gameValue)
    {
        if (entry.Value < 0f) entry.Value = gameValue;
    }

    private void ApplyTweaks()
    {
        if (_fpc != null)
        {
            _fpc.m_WalkSpeed = Plugin.WalkSpeed.Value;
            _fpc.m_RunSpeed = Plugin.RunSpeed.Value;
            _fpc.m_CrouchSpeed = Plugin.CrouchSpeed.Value;
            _fpc.m_JumpSpeed = Plugin.JumpSpeed.Value;
            _fpc.m_GravityMultiplier = Plugin.GravityMultiplier.Value;
            _fpc.sensitivityMultiplier = Plugin.Sensitivity.Value;
        }

        // Never write a zero reach - that would make items unpickable.
        if (_interact != null && _capturedInteract && Plugin.InteractDistance.Value > 0f)
        {
            _interact.itemInteractDistance = Plugin.InteractDistance.Value;
        }
    }

    private void ResetToGameDefaults()
    {
        Plugin.WalkSpeed.Value = _dWalk;
        Plugin.RunSpeed.Value = _dRun;
        Plugin.CrouchSpeed.Value = _dCrouch;
        Plugin.JumpSpeed.Value = _dJump;
        Plugin.GravityMultiplier.Value = _dGravity;
        Plugin.Sensitivity.Value = _dSens;
        if (_capturedInteract) Plugin.InteractDistance.Value = _dInteract;
        ApplyTweaks();
    }

    /// <summary>Puts the game's own numbers back when the user switches tweaks off.</summary>
    private void RestoreGameValues()
    {
        if (_fpc != null)
        {
            _fpc.m_WalkSpeed = _dWalk;
            _fpc.m_RunSpeed = _dRun;
            _fpc.m_CrouchSpeed = _dCrouch;
            _fpc.m_JumpSpeed = _dJump;
            _fpc.m_GravityMultiplier = _dGravity;
            _fpc.sensitivityMultiplier = _dSens;
        }
        if (_interact != null && _capturedInteract) _interact.itemInteractDistance = _dInteract;
    }

    // ----------------------------------------------------------------- input

    private bool TogglePressed()
    {
        var name = Plugin.ToggleKey.Value;

        try
        {
            var kb = Keyboard.current;
            if (kb != null && Enum.TryParse<Key>(name, true, out var key) && key != Key.None)
                return kb[key].wasPressedThisFrame;
        }
        catch { /* new input system unavailable, fall through */ }

        try
        {
            if (Enum.TryParse<KeyCode>(name, true, out var legacy))
                return Input.GetKeyDown(legacy);
        }
        catch { /* legacy input disabled in this build */ }

        return false;
    }

    // --------------------------------------------------------- teleport keys

    // ------------------------------------------------------- typed number entry

    private string _editingField;
    private string _editBuffer = "";
    private ConfigEntry<float> _editFloat;
    private ConfigEntry<int> _editInt;
    private float _editMin, _editMax;

    // Integer bounds are kept as ints on purpose. Routing them through the float pair
    // overflowed: (float)int.MaxValue rounds up to 2147483648f, and casting that back
    // with (int) wraps to int.MinValue, so the clamp drove every typed value to the
    // minimum instead of accepting it.
    private int _editIntMin, _editIntMax;

    /// <summary>
    /// Reads a typed number a key at a time.
    ///
    /// GUI.TextField is deliberately not used: this game's IMGUI is stripped - the same
    /// reason GUI.Slider throws "Method unstripping failed" - so a text field cannot be
    /// relied on. Buttons and labels are proven to work, so the field is drawn from those
    /// and the characters come straight off the keyboard device.
    /// </summary>
    [HideFromIl2Cpp]
    private void HandleTextEntry()
    {
        if (KeyDown(Key.Escape)) { _editingField = null; _status = "Đã hủy chỉnh sửa."; return; }

        if (KeyDown(Key.Enter) || KeyDown(Key.NumpadEnter)) { CommitEdit(); return; }

        if (KeyDown(Key.Backspace))
        {
            if (_editBuffer.Length > 0) _editBuffer = _editBuffer.Substring(0, _editBuffer.Length - 1);
            return;
        }

        for (int d = 0; d <= 9; d++)
        {
            if (Enum.TryParse<Key>("Digit" + d, out var dk) && KeyDown(dk)) { _editBuffer += d; return; }
            if (Enum.TryParse<Key>("Numpad" + d, out var nk) && KeyDown(nk)) { _editBuffer += d; return; }
        }

        // Only one decimal point, and only where fractions make sense.
        if (_editFloat != null && !_editBuffer.Contains("."))
        {
            if (KeyDown(Key.Period) || KeyDown(Key.NumpadPeriod)) _editBuffer += ".";
        }
    }

    [HideFromIl2Cpp]
    private void CommitEdit()
    {
        string text = _editBuffer;
        _editingField = null;

        if (string.IsNullOrWhiteSpace(text)) { _status = "Bạn chưa nhập gì."; return; }

        // Invariant culture: this machine formats floats with a comma, and parsing the
        // typed text with the local separator would reject a perfectly good "11.9".
        if (_editFloat != null &&
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        {
            _editFloat.Value = Mathf.Clamp(f, _editMin, _editMax);
            _status = $"Đã đặt thành {_editFloat.Value:0.##}.";
            return;
        }

        if (_editInt != null && int.TryParse(text, out int i))
        {
            _editInt.Value = Mathf.Clamp(i, _editIntMin, _editIntMax);
            _status = $"Đã đặt thành {_editInt.Value}.";
            return;
        }

        _status = $"'{text}' không phải là một số.";
    }

    /// <summary>
    /// True until GUI.TextField is proven unavailable. This game's IMGUI is partly
    /// stripped - GUI.Slider throws "Method unstripping failed" - so the real text field
    /// is attempted first and the hand-rolled one is used only if it actually fails.
    /// </summary>
    private static bool _textFieldOk = true;

    /// <summary>Editable value box: a real text field where possible, a button otherwise.</summary>
    [HideFromIl2Cpp]
    private void ValueBox(Rect r, string id, ConfigEntry<float> asFloat, ConfigEntry<int> asInt,
                          float min, float max, string display, int iMin = 0, int iMax = 0)
    {
        if (_textFieldOk)
        {
            try
            {
                string shown = _editingField == id ? _editBuffer : display;
                string typed = GUI.TextField(r, shown, 8);

                if (typed != null && typed != shown)
                {
                    _editingField = id;
                    _editBuffer = typed;
                    _editFloat = asFloat;
                    _editInt = asInt;
                    _editMin = min;
                    _editMax = max;
                    _editIntMin = iMin;
                    _editIntMax = iMax;

                    // Apply as soon as the text is a valid number - no Enter required.
                    ApplyTyped(typed, asFloat, asInt, min, max, iMin, iMax);
                }
                return;
            }
            catch (Exception e)
            {
                _textFieldOk = false;
                _editingField = null;
                LogOnce($"GUI.TextField unavailable ({e.Message}); falling back to button fields.");
            }
        }

        bool editing = _editingField == id;
        if (GUI.Button(r, editing ? _editBuffer + "|" : display) && !editing)
        {
            _editingField = id;
            _editBuffer = "";           // start empty so typing replaces the value
            _editFloat = asFloat;
            _editInt = asInt;
            _editMin = min;
            _editMax = max;
            _editIntMin = iMin;
            _editIntMax = iMax;
            _status = "Nhập một số, Enter để áp dụng, Esc để hủy.";
        }
    }

    [HideFromIl2Cpp]
    private void ApplyTyped(string text, ConfigEntry<float> asFloat, ConfigEntry<int> asInt,
                            float min, float max, int iMin, int iMax)
    {
        if (string.IsNullOrWhiteSpace(text)) return;   // mid-edit, nothing to apply yet

        // Invariant culture: this machine formats floats with a comma, so parsing typed
        // text with the local separator would reject a perfectly good "11.9".
        if (asFloat != null &&
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        {
            asFloat.Value = Mathf.Clamp(f, min, max);
        }
        else if (asInt != null && int.TryParse(text, out int i))
        {
            asInt.Value = Mathf.Clamp(i, iMin, iMax);
        }
    }

    /// <summary>
    /// Whether we are a client in somebody else's session, and so can never be the owner of
    /// an unowned-looking character. Asked of Netcode rather than of the game, because a late
    /// joiner's game-side state is not filled in.
    /// </summary>
    [HideFromIl2Cpp]
    private static bool IsRemoteClient()
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm != null && nm.IsListening && !nm.IsServer;
        }
        catch { return false; }
    }

    /// <summary>Our own network id, used so item RPCs are attributed to this player.</summary>
    [HideFromIl2Cpp]
    private ulong LocalClientId()
    {
        try
        {
            var pc = _fpc == null ? null : _fpc.m_PlayerCharacter;
            if (pc != null) return pc.OwnerClientId;
        }
        catch { /* fall through */ }
        return 0UL;
    }

    [HideFromIl2Cpp]
    private static bool KeyDown(Key k)
    {
        try
        {
            var kb = Keyboard.current;
            return kb != null && kb[k].wasPressedThisFrame;
        }
        catch { return false; }
    }

    /// <summary>Listens for the next keypress and assigns it to the pending location.</summary>
    [HideFromIl2Cpp]
    private void CaptureBinding()
    {
        if (KeyDown(Key.Escape))
        {
            _status = $"Đã hủy gán phím cho {_bindingFor}.";
            _bindingFor = null;
            return;
        }

        if (KeyDown(Key.Backspace) || KeyDown(Key.Delete))
        {
            _bindings.Remove(_bindingFor);
            PersistBindings();
            _status = $"Đã xóa phím tắt của {_bindingFor}.";
            _bindingFor = null;
            return;
        }

        foreach (var k in Teleporter.BindableKeys)
        {
            if (!KeyDown(k)) continue;

            if (_bindingFor == TakeKeySlot)
            {
                Plugin.BoxTakeOutKey.Value = k.ToString();
                Plugin.BoxTakeOutKey.ConfigFile.Save();
                _status = $"Phím lấy đồ ra khỏi thùng đã đặt thành {k}.";
                Plugin.Logger.LogInfo($"Box take-out key bound to {k}.");
                _bindingFor = null;
                return;
            }

            if (_bindingFor == SnapKeySlot)
            {
                Plugin.PlateSnapToggleKey.Value = k.ToString();
                Plugin.PlateSnapToggleKey.ConfigFile.Save();
                _status = $"Phím đổi chế độ canh vị trí đã đặt thành {k}.";
                Plugin.Logger.LogInfo($"Plate snap toggle key bound to {k}.");
                _bindingFor = null;
                return;
            }

            // The plate align key is not a location binding; it lives in its own config.
            if (_bindingFor == PlateKeySlot)
            {
                Plugin.PlateAlignKey.Value = k.ToString();
                Plugin.PlateAlignKey.ConfigFile.Save();
                _status = $"Phím canh thẳng trên đĩa đã đặt thành {k}.";
                Plugin.Logger.LogInfo($"Plate align key bound to {k}.");
                _bindingFor = null;
                return;
            }

            if (_bindingFor == CatKeySlot)
            {
                Plugin.CatSummonKey.Value = k.ToString();
                Plugin.CatSummonKey.ConfigFile.Save();
                _status = $"Phím gọi mèo đã đặt thành {k}.";
                Plugin.Logger.LogInfo($"Cat summon key bound to {k}.");
                _bindingFor = null;
                return;
            }

            // The box destroy key is not a location binding; it lives in its own config.
            if (_bindingFor == BoxKeySlot)
            {
                Plugin.BoxDestroyKey.Value = k.ToString();
                Plugin.BoxDestroyKey.ConfigFile.Save();
                _status = $"Phím hủy chồng đồ đã đặt thành {k}.";
                Plugin.Logger.LogInfo($"Box destroy key bound to {k}.");
                _bindingFor = null;
                return;
            }

            // A key can only drive one location, so drop it from any previous owner.
            var stale = new List<string>();
            foreach (var kv in _bindings)
                if (kv.Value == k && kv.Key != _bindingFor) stale.Add(kv.Key);
            foreach (var s in stale) _bindings.Remove(s);

            _bindings[_bindingFor] = k;
            PersistBindings();
            _status = $"Đã gán {k} cho {_bindingFor}.";
            Plugin.Logger.LogInfo($"Bound {k} -> {_bindingFor}");
            _bindingFor = null;
            return;
        }
    }

    [HideFromIl2Cpp]
    private void CheckTeleportHotkeys()
    {
        if (!Plugin.HotkeysEnabled.Value || _fpc == null || _bindings.Count == 0) return;

        foreach (var kv in _bindings)
        {
            if (!KeyDown(kv.Value)) continue;

            var loc = _locations.Find(l => l.Name == kv.Key);
            if (loc == null)
            {
                // The list may not have been scanned yet in this scene.
                RefreshLocations();
                loc = _locations.Find(l => l.Name == kv.Key);
            }

            if (loc != null) Jump(loc);
            else _status = $"Không có địa điểm nào tên {kv.Key} trong màn này.";
            return;
        }
    }

    [HideFromIl2Cpp]
    private void PersistBindings()
    {
        Plugin.HotkeyBindings.Value = Teleporter.SaveBindings(_bindings);
        Plugin.HotkeyBindings.ConfigFile.Save();
    }

    [HideFromIl2Cpp]
    private void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        _editingField = null;   // do not leave a field capturing keys after closing

        // Ahead of everything else, and outside the FreeCursorWhenOpen branches below:
        // the interaction guard is its own setting, so leaving the player unable to touch
        // anything must not depend on which cursor options happen to be on.
        if (!open) RestoreGameInteraction();

        if (open)
        {
            if (!Plugin.FreeCursorWhenOpen.Value) return;

            _prevLock = Cursor.lockState;
            _prevCursorVisible = Cursor.visible;
            _prevCanRotate = _fpc == null || _fpc.canRotateView;
            _prevCanMove = _fpc == null || _fpc.CanMove;
            _tookOverCursor = true;
            _cursorSettled = false;
            _relockCount = 0;
        }
        else if (_tookOverCursor)
        {
            // Restore regardless of the current setting - if the user flipped
            // FreeCursorWhenOpen while the panel was open, we still own this state.
            _tookOverCursor = false;

            // Hand the cursor back to the game before restoring, so its manager
            // resumes from the state we put back rather than fighting us for a frame.
            RestoreGameCursorControl();

            Cursor.lockState = _prevLock;
            Cursor.visible = _prevCursorVisible;
            if (_fpc != null)
            {
                _fpc.canRotateView = _prevCanRotate;
                _fpc.CanMove = _prevCanMove;
            }
        }
    }

    // ------------------------------------------------------------------- gui

    // The UI is Vietnamese, and IMGUI's built-in font is not guaranteed to carry the
    // diacritics - a missing glyph renders as a blank box, which would make whole labels
    // unreadable rather than merely ugly. A dynamic OS font is asked for once and used if
    // it resolves; if it does not, the built-in font is left in place unchanged.
    private static Font _font;
    private static bool _fontTried;

    [HideFromIl2Cpp]
    private static Font UiFont()
    {
        if (_fontTried) return _font;
        _fontTried = true;

        // Segoe UI first: it ships with Windows and covers Vietnamese properly. Arial and
        // Tahoma are the fallbacks for a machine that somehow lacks it.
        foreach (var name in new[] { "Segoe UI", "Arial", "Tahoma" })
        {
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(name, 14);
                if (f != null)
                {
                    _font = f;
                    Plugin.Logger.LogInfo($"UI font: using '{name}' for Vietnamese text.");
                    return _font;
                }
            }
            catch { /* try the next one */ }
        }

        Plugin.Logger.LogWarning("UI font: no OS font resolved; falling back to the built-in one.");
        return null;
    }

    // ------------------------------------------------------------------ theme

    private static Texture2D _fillTex;
    private static Texture2D _edgeTex;
    private static GUISkin _skin;
    private static bool _skinTried;

    private static readonly Color PanelFill = new Color(0.05f, 0.05f, 0.07f, 0.95f);
    private static readonly Color PanelEdge = new Color(0.55f, 0.58f, 0.66f, 0.9f);
    private static readonly Color TextBright = new Color(0.94f, 0.95f, 0.97f);

    [HideFromIl2Cpp]
    private static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.hideFlags = HideFlags.HideAndDontSave;   // survives scene loads, never serialised
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    /// <summary>
    /// A near-opaque dark background with a light edge.
    ///
    /// The default IMGUI box is mostly transparent, so every panel was being read against
    /// whatever the kitchen looked like behind it - which is what made the overlay so hard
    /// to see. Painting our own ground first means the text contrast no longer depends on
    /// where the player happens to be standing.
    /// </summary>
    [HideFromIl2Cpp]
    private void Backdrop(Rect r)
    {
        try
        {
            if (_fillTex == null) _fillTex = Solid(PanelFill);
            if (_edgeTex == null) _edgeTex = Solid(PanelEdge);

            GUI.DrawTexture(r, _fillTex);

            const float t = 1f;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), _edgeTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), _edgeTex);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), _edgeTex);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), _edgeTex);
        }
        catch (Exception e)
        {
            // Losing the backdrop is survivable - the box below still frames the panel.
            LogOnce($"Backdrop failed: {e.GetType().Name}: {e.Message}");
            try { GUI.Box(r, GUIContent.none); } catch { }
        }
    }

    /// <summary>
    /// Our own copy of the IMGUI skin, with light text throughout.
    ///
    /// A copy rather than edits to GUI.skin itself: the skin is shared global state, and
    /// its styles are objects, so changing a colour on it would restyle every other IMGUI
    /// draw in the game and could not be put back by restoring a single field. Swapping
    /// the whole skin for the duration of our drawing is one save and one restore.
    /// </summary>
    [HideFromIl2Cpp]
    private static GUISkin UiSkin()
    {
        if (_skinTried) return _skin;
        _skinTried = true;

        try
        {
            var copy = UnityEngine.Object.Instantiate(GUI.skin);
            if (copy == null) return null;

            copy.hideFlags = HideFlags.HideAndDontSave;

            var font = UiFont();
            if (font != null) copy.font = font;

            // The default skin writes in near-black, which is unreadable on the dark
            // ground the panels now paint for themselves.
            Recolour(copy.label);
            Recolour(copy.box);
            Recolour(copy.button);
            Recolour(copy.toggle);
            Recolour(copy.textField);
            Recolour(copy.window);

            _skin = copy;
            Plugin.Logger.LogInfo("UI theme: using a light-on-dark copy of the IMGUI skin.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"UI theme: could not copy the skin ({e.Message}); " +
                                     "falling back to the font swap alone.");
            _skin = null;
        }
        return _skin;
    }

    [HideFromIl2Cpp]
    private static void Recolour(GUIStyle s)
    {
        if (s == null) return;

        // Every state, not just normal: a button reverts to the skin's dark text the
        // moment the mouse is over it otherwise.
        foreach (var state in new[] { s.normal, s.hover, s.active, s.focused,
                                      s.onNormal, s.onHover, s.onActive, s.onFocused })
        {
            if (state == null) continue;
            try { state.textColor = TextBright; } catch { }
        }
    }

    private void OnGUI()
    {
        // Swap our skin in around our own drawing only, and put the game's back
        // afterwards. GUI.skin is global state - leaving it changed would restyle
        // anything else that draws with IMGUI this frame.
        GUISkin previous = null;
        try
        {
            var skin = UiSkin();
            if (skin != null) { previous = GUI.skin; GUI.skin = skin; }
        }
        catch { /* not fatal: the text just renders in the game's own style */ }

        try
        {
            DrawAll();
        }
        finally
        {
            try { if (previous != null) GUI.skin = previous; }
            catch { }
        }
    }

    [HideFromIl2Cpp]
    private void DrawAll()
    {
        // Drawn before the early return: the destroy hold happens with the panel closed,
        // standing at a box.
        try { DrawHoldBar(); }
        catch (Exception e) { LogOnce($"Hold bar failed: {e.GetType().Name}: {e.Message}"); }

        // Likewise the dish overlay - it is for playing with the panel shut.
        try { DrawDishHud(); }
        catch (Exception e) { LogOnce($"Dish HUD failed: {e.GetType().Name}: {e.Message}"); }

        // Also above the panel check: the cabinet menu only ever appears while playing, since
        // it needs the crosshair to be on a cabinet.
        try { DrawStockMenu(); }
        catch (Exception e) { LogOnce($"Stock menu failed: {e.GetType().Name}: {e.Message}"); }

        if (!_open) return;

        // OnGUI is the last thing to run in a frame, so this is the final word on
        // the cursor if anything else re-locked it after LateUpdate.
        ApplyCursorState();

        // One stripped Unity method must not take the whole panel down with it.
        try { DrawPanel(); }
        catch (Exception e) { LogOnce($"OnGUI failed: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>
    /// Progress bar for the destroy hold. The game's own bar (ProgressBarIsMaxAlt) never
    /// completed when driven from here, so this draws one rather than leaving the hold
    /// with no feedback at all.
    /// </summary>
    [HideFromIl2Cpp]
    private void DrawHoldBar()
    {
        float t = BoxTweaks.HoldProgress;
        if (t <= 0f) return;

        const float barW = 280f;
        const float barH = 16f;

        float x = (Screen.width - barW) * 0.5f;
        float y = Screen.height * 0.62f;

        Backdrop(new Rect(x - 8f, y - 26f, barW + 16f, barH + 34f));
        GUI.Label(new Rect(x, y - 24f, barW, 20f), BoxTweaks.HoldLabel ?? "");

        GUI.Box(new Rect(x, y, barW, barH), GUIContent.none);
        if (t > 0f) GUI.Box(new Rect(x + 2f, y + 2f, (barW - 4f) * t, barH - 4f), GUIContent.none);
    }

    // ------------------------------------------------------- today's dishes HUD

    private const float HudWidth = 256f;
    private const float HudRow = 20f;
    private const float HudMargin = 12f;

    private bool _hudDragging;
    private Vector2 _hudDragOffset;

    /// <summary>Where the panel sits now, kept here so a drag can move it every frame
    /// without writing the config file on every one of them.</summary>
    private Vector2 _hudPos = new Vector2(float.NaN, float.NaN);

    /// <summary>
    /// The panel's rect for a given height, seeding the top right corner the first time.
    ///
    /// The default cannot be a stored coordinate: where "top right" is depends on the
    /// resolution, and a coordinate saved at one resolution puts the panel off-screen at
    /// another. -1 in the config means "top right, wherever that is now".
    /// </summary>
    [HideFromIl2Cpp]
    private Rect HudRect(float height)
    {
        if (float.IsNaN(_hudPos.x))
        {
            float cx = Plugin.DishHudX.Value;
            float cy = Plugin.DishHudY.Value;

            _hudPos = cx < 0f || cy < 0f
                ? new Vector2(Screen.width - HudWidth - HudMargin, HudMargin)
                : new Vector2(cx, cy);
        }

        // Re-clamped every frame rather than only on release: a resolution change would
        // otherwise strand a panel that was dropped near the old edge.
        _hudPos.x = Mathf.Clamp(_hudPos.x, 0f, Mathf.Max(0f, Screen.width - HudWidth));
        _hudPos.y = Mathf.Clamp(_hudPos.y, 0f, Mathf.Max(0f, Screen.height - height));

        return new Rect(_hudPos.x, _hudPos.y, HudWidth, height);
    }

    /// <summary>
    /// Moves the panel while the tweak overlay is open.
    ///
    /// Only while it is open: with it closed the cursor belongs to the game, so a drag
    /// here would fight the camera rather than move anything. The config is written on
    /// release rather than on every frame of the drag - BepInEx saves the file when a
    /// value is set, and doing that per frame would hit the disk hundreds of times.
    /// </summary>
    [HideFromIl2Cpp]
    private void HandleHudDrag(Rect r)
    {
        if (!_open)
        {
            _hudDragging = false;
            return;
        }

        var e = Event.current;
        if (e == null) return;

        switch (e.type)
        {
            case EventType.MouseDown when r.Contains(e.mousePosition):
                _hudDragging = true;
                _hudDragOffset = e.mousePosition - new Vector2(r.x, r.y);
                e.Use();
                break;

            case EventType.MouseDrag when _hudDragging:
                _hudPos = e.mousePosition - _hudDragOffset;
                e.Use();
                break;

            case EventType.MouseUp when _hudDragging:
                _hudDragging = false;
                Plugin.DishHudX.Value = _hudPos.x;
                Plugin.DishHudY.Value = _hudPos.y;
                e.Use();
                break;
        }
    }

    /// <summary>
    /// One row per dish on today's menu, showing how many portions are plated and ready to
    /// serve, coloured by how close that is to running out.
    ///
    /// Drawn whether or not the tweak panel is open - the whole point is to glance at it
    /// mid-service - but only draggable while it is open.
    /// </summary>
    [HideFromIl2Cpp]
    private void DrawDishHud()
    {
        if (!Plugin.DishHudEnabled.Value) return;

        var entries = DishStatus.Entries;
        if (entries.Count == 0) return;

        // Hidden dishes are dropped here rather than in DishStatus, so the panel can still
        // list every dish on the menu to tick and untick.
        int visible = 0;
        foreach (var e in entries) if (!e.Hidden) visible++;
        if (visible == 0) return;

        float h = Pad + HudRow + visible * HudRow + Pad;
        var r = HudRect(h);

        HandleHudDrag(r);
        r = HudRect(h);   // the drag above may have just moved it

        Backdrop(r);

        float ix = r.x + Pad;
        float iw = HudWidth - 2f * Pad;
        float iy = r.y + Pad;

        GUI.Label(new Rect(ix, iy, iw, HudRow),
                  _open ? "<b>Món hôm nay</b>  <size=10>(kéo để di chuyển)</size>"
                        : "<b>Món hôm nay</b>");
        iy += HudRow;

        var previous = GUI.contentColor;
        try
        {
            const float numW = 22f;
            const float countW = 40f;

            // Numbered over what is actually drawn, so the list reads 1..n with no gaps
            // where a hidden dish would have been.
            int n = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Hidden) continue;

                n++;
                GUI.contentColor = HudColour(e.Level);

                GUI.Label(new Rect(ix, iy, numW, HudRow), $"{n}.");
                GUI.Label(new Rect(ix + numW, iy, iw - numW - countW, HudRow), e.Name);
                GUI.Label(new Rect(ix + iw - countW, iy, countW, HudRow),
                          e.Servings < 0 ? "?" : e.Servings.ToString());

                iy += HudRow;
            }
        }
        finally
        {
            // GUI.contentColor is global; leaving it tinted would recolour the tweak
            // panel drawn right after this.
            GUI.contentColor = previous;
        }
    }

    /// <summary>
    /// Status colours, picked to sit on the near-black backdrop rather than on whatever
    /// the kitchen happens to look like behind them.
    /// </summary>
    [HideFromIl2Cpp]
    private static Color HudColour(DishStatus.Level level) => level switch
    {
        DishStatus.Level.Out => new Color(1f, 0.44f, 0.40f),
        DishStatus.Level.Low => new Color(1f, 0.85f, 0.28f),
        DishStatus.Level.Full => new Color(0.48f, 1f, 0.52f),
        _ => new Color(0.78f, 0.78f, 0.82f),
    };

    private void DrawPanel()
    {
        HandleDrag();

        // Both the height calculation and the drawing below must see the same menu, so
        // the snapshot is taken once, before either of them looks at it.
        if (IsMenuTab(_tab)) RefreshMenuCache(false);

        // Sized from what the last draw of this tab actually came to, not from a
        // prediction. The old ContentHeight() was a second, hand-written copy of every
        // layout in the file - each section's height re-derived by counting rows in a
        // comment - so it drifted out of step the moment any section changed, and the
        // panel cropped its own content. Measuring cannot drift.
        int slot = (int)_tab;
        float known = _measured[slot];
        _panel.height = TitleH + TabRows * TabH + Pad + (known > 0f ? known : 2f * Row) + Pad;

        // Never taller than the screen, or the title bar can end up somewhere it cannot be
        // grabbed to drag the panel back.
        _panel.height = Mathf.Min(_panel.height, Screen.height - 2f * Pad);

        Backdrop(_panel);

        float x = _panel.x + Pad;
        float y = _panel.y + Pad;
        float w = PanelWidth - 2f * Pad;

        GUI.Label(new Rect(x, y, w, 22f), $"<b>Kebab Tweaks</b>   —   {Plugin.ToggleKey.Value} để đóng");
        y += TitleH;

        y = DrawTabs(x, y, w);
        y += Pad;

        float top = y;
        float bottom = y;

        switch (_tab)
        {
            case Tab.Player: bottom = DrawPlayerTab(x, y, w); break;
            case Tab.Teleport: bottom = DrawTeleportTab(x, y, w); break;
            case Tab.Interactions: bottom = DrawInteractionsTab(x, y, w); break;
            case Tab.Kitchen: bottom = DrawKitchenTab(x, y, w); break;
            case Tab.Placing: bottom = DrawPlacingTab(x, y, w); break;
            case Tab.Dishes: bottom = DrawDishesTab(x, y, w); break;
            case Tab.Today: bottom = DrawTodayTab(x, y, w); break;
            case Tab.Events: bottom = DrawEventsTab(x, y, w); break;
        }

        // Only the repaint pass has final positions; measuring on every event would let a
        // layout pass with stale data shrink the panel for a frame.
        if (Event.current == null || Event.current.type == EventType.Repaint)
            _measured[slot] = Mathf.Max(2f * Row, bottom - top);
    }

    /// <summary>
    /// Content height per tab, as measured on the last repaint. Indexed by <see cref="Tab"/>
    /// so switching tabs does not size the panel from the previous tab's content.
    /// </summary>
    /// Seeded rather than left at zero so the first draw of a tab is roughly the right
    /// size instead of collapsing to two rows for one frame every time you switch.
    private readonly float[] _measured = { 320f, 320f, 320f, 340f, 538f, 360f, 400f, 300f };

    /// <summary>Whether a tab draws anything built from the menu snapshot.</summary>
    [HideFromIl2Cpp]
    private static bool IsMenuTab(Tab tab) => tab == Tab.Dishes || tab == Tab.Today;

    [HideFromIl2Cpp]
    private float DrawTabs(float x, float y, float w)
    {
        float bw = w / TabsPerRow;

        for (int i = 0; i < TabLabels.Length; i++)
        {
            int col = i % TabsPerRow;
            int row = i / TabsPerRow;

            DrawTabButton(new Rect(x + col * bw, y + row * TabH, bw, TabH), (Tab)i, TabLabels[i]);
        }

        return y + TabRows * TabH;
    }

    [HideFromIl2Cpp]
    private void DrawTabButton(Rect r, Tab tab, string label)
    {
        bool active = _tab == tab;
        var prev = GUI.backgroundColor;

        // Tint the inactive tab down so the active one reads as selected.
        GUI.backgroundColor = active ? prev : new Color(prev.r, prev.g, prev.b, prev.a * 0.4f);
        if (GUI.Button(r, active ? $"<b>{label}</b>" : label))
        {
            _tab = tab;
            // Locations go stale across scene loads; refresh on entry if we have none.
            if (tab == Tab.Teleport && _locations.Count == 0) RefreshLocations();
            // The menu can have changed on the tablet since we last looked.
            if (IsMenuTab(tab)) RefreshMenuCache(true);
        }
        GUI.backgroundColor = prev;
    }

    // ------------------------------------------------------------ player tab

    [HideFromIl2Cpp]
    private float DrawPlayerTab(float x, float y, float w)
    {
        if (!_captured)
        {
            GUI.Label(new Rect(x, y, w, Row), "Đang chờ người chơi xuất hiện...");
            GUI.Label(new Rect(x, y + Row, w, Row), "Hãy vào nhà hàng rồi mở lại bảng này.");
            return y + 2f * Row;
        }

        bool on = GUI.Toggle(new Rect(x, y, w, Row), Plugin.Enabled.Value, "  Bật tinh chỉnh");
        if (on != Plugin.Enabled.Value)
        {
            Plugin.Enabled.Value = on;
            if (!on) RestoreGameValues();
        }
        y += Row + Pad;

        GUI.enabled = Plugin.Enabled.Value;

        y = NumberRow(x, y, w, "Tốc độ đi bộ", Plugin.WalkSpeed, 0f);
        y = NumberRow(x, y, w, "Tốc độ chạy", Plugin.RunSpeed, 0f);
        y = NumberRow(x, y, w, "Tốc độ ngồi", Plugin.CrouchSpeed, 0f);
        y = NumberRow(x, y, w, "Lực nhảy", Plugin.JumpSpeed, 0f);
        y = NumberRow(x, y, w, "Trọng lực", Plugin.GravityMultiplier, 0f);
        y = NumberRow(x, y, w, "Độ nhạy chuột", Plugin.Sensitivity, 0f);

        GUI.enabled = true;
        y += Pad;

        float bw = (w - Pad) / 2f;
        if (GUI.Button(new Rect(x, y, bw, Row), "Khôi phục mặc định"))
            ResetToGameDefaults();
        if (GUI.Button(new Rect(x + bw + Pad, y, bw, Row), "Lưu vào tệp cấu hình"))
        {
            Plugin.Enabled.ConfigFile.Save();
            Plugin.Logger.LogInfo("Config saved.");
        }
        return y + Row;
    }

    // ------------------------------------------------------ interactions tab

    /// <summary>
    /// Everything about handling objects: how far you can reach, how much you can carry,
    /// and the behaviour of shelves, boxes, seasoning and plating. Movement and the tablet
    /// menu live elsewhere - they are not about interacting with things.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawInteractionsTab(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Tầm với và mang vác</b>");
        y += Row;

        if (_capturedInteract)
        {
            GUI.enabled = Plugin.Enabled.Value;
            y = NumberRow(x, y, w, "Tầm tương tác", Plugin.InteractDistance, 0.1f);
            GUI.enabled = true;
        }
        else
        {
            GUI.Label(new Rect(x, y, w, Row), "<i>Tầm tương tác: đang chờ người chơi...</i>");
            y += Row;
        }

        bool carry = GUI.Toggle(new Rect(x, y, w, Row), Plugin.CarryEnabled.Value,
                                $"  Tùy chỉnh số vật phẩm cầm trên tay (mặc định {CarryTweaks.DefaultCount})");
        if (carry != Plugin.CarryEnabled.Value)
        {
            Plugin.CarryEnabled.Value = carry;
            if (!carry) CarryTweaks.Restore(_interact);
        }
        y += Row;

        GUI.enabled = Plugin.CarryEnabled.Value;
        y = IntRow(x, y, w, "Số vật phẩm trên tay", Plugin.CarryCount, 1, CarryTweaks.MaxSupported);
        GUI.enabled = true;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{CarryTweaks.Status}</i>");
        y += Row;

        // The host validates the queue slot against its own copy of your character
        // (OnItemAddedToQueueServerRpc), so an unmodded host caps you at the shipped four
        // however high this is set, and a modded host with a lower number caps you there.
        GUI.Label(new Rect(x, y, w, Row),
                  "<i>Chơi mạng: chủ phòng cũng phải bật mục này - máy chủ mới quyết định số ô.</i>");
        y += Row;

        bool multi = GUI.Toggle(new Rect(x, y, w, Row), Plugin.MultiPickEnabled.Value,
                                "  Giữ Shift + lăn chuột để đánh dấu nhiều vật phẩm cùng loại");
        if (multi != Plugin.MultiPickEnabled.Value) Plugin.MultiPickEnabled.Value = multi;
        y += Row;

        GUI.enabled = Plugin.MultiPickEnabled.Value;
        y = NumberRow(x, y, w, "Bán kính đánh dấu (m)", Plugin.MultiPickRadius, 0.1f);

        bool border = GUI.Toggle(new Rect(x, y, w, Row), Plugin.MultiPickOutline.Value,
                                 "  Dùng viền chọn của game thay vì tô màu xanh lá");
        if (border != Plugin.MultiPickOutline.Value) Plugin.MultiPickOutline.Value = border;
        y += Row;
        GUI.enabled = true;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{MultiPick.Status}</i>");
        y += Row + Pad;

        return DrawShoppingSection(x, y, w);
    }

    // ------------------------------------------------------------ kitchen tab

    /// <summary>Everything that happens to an ingredient between the box and the plate.</summary>
    [HideFromIl2Cpp]
    private float DrawKitchenTab(float x, float y, float w)
    {
        y = DrawSeasoningSection(x, y, w);
        y = DrawFishSection(x, y, w);
        y = DrawSlicerSection(x, y, w);
        return DrawBoxSection(x, y, w);
    }

    // ------------------------------------------------------------ placing tab

    /// <summary>Where things go down: plating, the placement grid, the plate cabinets, and
    /// the last step of all - a finished plate onto a service counter.</summary>
    [HideFromIl2Cpp]
    private float DrawPlacingTab(float x, float y, float w)
    {
        y = DrawPlatingSection(x, y, w);
        return DrawServingSection(x, y, w);
    }

    // -------------------------------------------------------------- menu tab

    /// <summary>Category buttons per row, matching the tablet's own tab strip.</summary>
    private const int CategoriesPerRow = 4;

    /// <summary>Dishes shown at once in the picker. Paged rather than scrolled - this
    /// game's IMGUI is partly stripped, and a scroll view is not worth betting on.</summary>
    private const int MenuPageRows = 6;

    // A snapshot, refreshed on a timer and after every edit. Rebuilding the addable list
    // costs a walk of the entire recipe catalogue, and the panel would otherwise do it
    // twice a frame - once to measure itself and once to draw.
    private readonly List<Recipe> _menuToday = new();
    private readonly List<Recipe> _menuAddable = new();
    private float _menuRefreshAt;

    /// <summary>-1 is "every category"; anything else indexes MenuTweaks.Categories.</summary>
    private int _menuCategory = -1;
    private int _menuPage;

    // Edits are deferred to the end of the draw so the list being drawn is never the list
    // being changed.
    private Recipe _menuAdd;
    private Recipe _menuRemove;
    private int _menuMoveFrom = -1;
    private int _menuMoveDelta;
    private bool _menuDirty;

    [HideFromIl2Cpp]
    private static int CategoryRows() =>
        (MenuTweaks.Categories.Length + CategoriesPerRow - 1) / CategoriesPerRow;

    [HideFromIl2Cpp]
    private FoodTag? SelectedCategory() =>
        _menuCategory < 0 || _menuCategory >= MenuTweaks.Categories.Length
            ? (FoodTag?)null
            : MenuTweaks.Categories[_menuCategory];

    /// <summary>
    /// The picker's order, read straight from the config so it survives a restart. Anything
    /// unrecognised falls back to sorting by name rather than throwing at a hand-edited
    /// config file.
    /// </summary>
    [HideFromIl2Cpp]
    private DishSort SelectedSort() => Plugin.MenuSortMode.Value switch
    {
        1 => DishSort.PriceHigh,
        2 => DishSort.PriceLow,
        _ => DishSort.Name,
    };

    [HideFromIl2Cpp]
    private static string SortName(DishSort sort) => sort switch
    {
        DishSort.PriceHigh => "Giá cao → thấp",
        DishSort.PriceLow => "Giá thấp → cao",
        _ => "Tên A → Z",
    };

    [HideFromIl2Cpp]
    private string SelectedCategoryName() =>
        _menuCategory < 0 || _menuCategory >= MenuTweaks.Categories.Length
            ? "Tất cả"
            : MenuTweaks.Label(MenuTweaks.Categories[_menuCategory]);

    [HideFromIl2Cpp]
    private void RefreshMenuCache(bool force)
    {
        if (!force && Time.unscaledTime < _menuRefreshAt) return;
        _menuRefreshAt = Time.unscaledTime + 1f;

        _menuToday.Clear();
        _menuToday.AddRange(MenuTweaks.TodaysMenu());

        _menuAddable.Clear();
        _menuAddable.AddRange(MenuTweaks.Addable(SelectedCategory(), SelectedSort()));

        // Any display name that more than one addable dish answers to. Two rows reading
        // the same thing is how "Pie" passed itself off as "Bánh mâm xôi" - the row was
        // honest about which recipe it stood for, and the label was not. Rows like these
        // get their internal key appended so the two can be told apart, whatever else is
        // filtering them.
        _ambiguousNames.Clear();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var r in _menuAddable)
        {
            string shown = MenuTweaks.DisplayName(r);
            if (!seen.Add(shown)) _ambiguousNames.Add(shown);
        }

        int pages = Mathf.Max(1, (_menuAddable.Count + MenuPageRows - 1) / MenuPageRows);
        _menuPage = Mathf.Clamp(_menuPage, 0, pages - 1);
    }

    /// <summary>Display names shared by more than one addable dish; see RefreshMenuCache.</summary>
    private readonly HashSet<string> _ambiguousNames =
        new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// What to print for a dish in the picker: its name, plus the game's own key when the
    /// name alone would not say which dish the row is.
    /// </summary>
    [HideFromIl2Cpp]
    private string PickerLabel(Recipe r)
    {
        string shown = MenuTweaks.DisplayName(r);
        if (!_ambiguousNames.Contains(shown)) return shown;

        return $"{shown}   <i><color=#b0b0b0>{MenuTweaks.Key(r)}</color></i>";
    }

    /// <summary>Choosing what to put on the menu.</summary>
    [HideFromIl2Cpp]
    private float DrawDishesTab(float x, float y, float w)
    {
        y = DrawQuickAdd(x, y, w);
        y = DrawDishPicker(x, y, w);

        GUI.Label(new Rect(x, y, w, Row), $"<i>{MenuTweaks.Status}</i>");
        y += Row;

        ApplyMenuEdits();
        return y;
    }

    /// <summary>
    /// Today's menu itself: what is on it, how to save and restore it, and the on-screen
    /// tracker that watches it.
    ///
    /// The list goes first here, unlike in the old combined tab where it had to go last to
    /// stop the controls below it moving as dishes came and went. Nothing under it now depends
    /// on being in the same place from one draw to the next.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawTodayTab(float x, float y, float w)
    {
        y = DrawTodaysMenu(x, y, w);
        y = DrawPresetSection(x, y, w);
        y = DrawDishHudSection(x, y, w);

        GUI.Label(new Rect(x, y, w, Row), $"<i>{MenuTweaks.Status}</i>");
        y += Row;

        ApplyMenuEdits();
        return y;
    }

    // ------------------------------------------------------------- events tab

    /// <summary>Things that happen to the restaurant rather than to a dish.</summary>
    [HideFromIl2Cpp]
    private float DrawEventsTab(float x, float y, float w) => DrawCatSection(x, y, w);

    /// <summary>Captions for <see cref="CatVisits.Mode"/>, indexed by its value.</summary>
    private static readonly string[] CatModeLabels =
    {
        "Hằng ngày", "Không bao giờ", "Cách N ngày", "Chỉ khi gọi",
    };

    /// <summary>
    /// The cat's schedule.
    ///
    /// The controls are always this machine's own choice, even on a client where the host is
    /// what decides - a control that showed somebody else's answer would spring back under
    /// the player's hand. What the restaurant is actually running on is the status line
    /// underneath, so the two are never confused for each other.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawCatSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Mèo ghé thăm</b>");
        y += Row;

        bool on = GUI.Toggle(new Rect(x, y, w, Row), Plugin.CatVisitsEnabled.Value,
                             "  Tự quyết khi nào mèo ghé (tắt = ngẫu nhiên như game gốc)");
        if (on != Plugin.CatVisitsEnabled.Value)
        {
            Plugin.CatVisitsEnabled.Value = on;
            if (!on) CatVisits.Restore();
        }
        y += Row + 2f;

        GUI.enabled = Plugin.CatVisitsEnabled.Value;

        // Captured around the whole block: these three are one schedule, and the host is told
        // about it as one thing rather than a field at a time.
        int wasMode = Plugin.CatVisitMode.Value;
        int wasDays = Plugin.CatVisitInterval.Value;
        float wasHour = Plugin.CatVisitHour.Value;

        float bw = w / CatModeLabels.Length;
        for (int i = 0; i < CatModeLabels.Length; i++)
        {
            bool active = wasMode == i;
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = active
                ? prevBg
                : new Color(prevBg.r, prevBg.g, prevBg.b, prevBg.a * 0.4f);

            if (GUI.Button(new Rect(x + i * bw, y, bw, Row),
                           active ? $"<b>{CatModeLabels[i]}</b>" : CatModeLabels[i]))
                Plugin.CatVisitMode.Value = i;

            GUI.backgroundColor = prevBg;
        }
        y += Row + 2f;

        if (Plugin.CatVisitMode.Value == (int)CatVisits.Mode.EveryNDays)
            y = IntRow(x, y, w, "Cách nhau mấy ngày", Plugin.CatVisitInterval, 1, 30);

        y = NumberRow(x, y, w, "Giờ mèo đến", Plugin.CatVisitHour, -1f, 23.99f);

        string arrival = CatVisits.ArrivalText;
        string clock = CatVisits.ClockText;
        GUI.Label(new Rect(x, y, w, Row),
                  arrival == null
                      ? $"<i>-1 = giữ nguyên giờ của game.{(clock == null ? "" : $" Đồng hồ: {clock}.")}</i>"
                      : $"<i>Mèo đến lúc {arrival}.{(clock == null ? "" : $" Đồng hồ: {clock}.")}</i>");
        y += Row + 2f;

        if (Plugin.CatVisitMode.Value != wasMode ||
            Plugin.CatVisitInterval.Value != wasDays ||
            !Mathf.Approximately(Plugin.CatVisitHour.Value, wasHour))
        {
            CatVisits.ApplySchedule((CatVisits.Mode)Plugin.CatVisitMode.Value,
                                    Plugin.CatVisitInterval.Value,
                                    Plugin.CatVisitHour.Value,
                                    "người chơi đổi lịch");
            _status = "Đã đặt lịch mèo ghé.";
        }

        // Today's answer, whatever the schedule says. This is the control that makes the
        // feature usable on the day rather than from tomorrow: the game has already rolled by
        // the time the panel is open, so a probability would be too late to matter.
        bool coming = CatVisits.ComingToday;
        bool wants = GUI.Toggle(new Rect(x, y, w, Row), coming, "  Hôm nay mèo ghé");
        if (wants != coming)
        {
            CatVisits.SetComingToday(wants);
            _status = wants ? "Đã hẹn mèo ghé hôm nay." : "Hôm nay mèo sẽ không ghé.";
        }
        y += Row + 2f;

        float half = (w - Pad) / 2f;
        if (GUI.Button(new Rect(x, y, half, Row), "Gọi mèo đến ngay"))
        {
            CatVisits.SummonNow();
            _status = "Đã gọi mèo.";
        }

        bool armingCat = _bindingFor == CatKeySlot;
        var prevCat = GUI.backgroundColor;
        if (armingCat) GUI.backgroundColor = new Color(1f, 0.8f, 0.3f, prevCat.a);

        float keyW = half - 30f;
        string keyName = string.IsNullOrWhiteSpace(Plugin.CatSummonKey.Value)
            ? "chưa gán phím"
            : Plugin.CatSummonKey.Value;

        if (GUI.Button(new Rect(x + half + Pad, y, keyW, Row),
                       armingCat ? "nhấn phím" : keyName))
        {
            _bindingFor = armingCat ? null : CatKeySlot;
            _status = armingCat ? "Đã hủy gán phím." : "Nhấn một phím để gán, Esc để hủy.";
        }
        GUI.backgroundColor = prevCat;

        if (GUI.Button(new Rect(x + half + Pad + keyW + 4f, y, 26f, Row), "✕"))
        {
            Plugin.CatSummonKey.Value = "";
            Plugin.CatSummonKey.ConfigFile.Save();
            _status = "Đã bỏ phím gọi mèo.";
        }
        y += Row;

        GUI.enabled = true;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{CatVisits.Status}.</i>");
        y += Row;

        // The line that keeps a client honest: the controls above are what this machine has
        // asked for, and only the host can actually put a cat in the restaurant.
        GUI.Label(new Rect(x, y, w, Row), CatVisits.FollowsHost
            ? "<i>Chơi mạng: chỉ chủ phòng thả được mèo, nên các lựa chọn trên được gửi " +
              "sang cho chủ phòng - chủ phòng cũng phải bật mục này.</i>"
            : "<i>Chơi mạng: bạn là chủ phòng, lịch này áp dụng cho cả phòng.</i>");
        y += Row;

        return y + Pad;
    }

    /// <summary>
    /// Today's menu, with the controls the tablet has - remove, and move within the list.
    ///
    /// Having our own list here is not only convenience: a dish the tablet does not draw
    /// a row for cannot be removed there at all, so this is the way back out of that.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawTodaysMenu(float x, float y, float w)
    {
        int max = MenuTweaks.MaxOnMenu();
        GUI.Label(new Rect(x, y, w, Row), max > 0
            ? $"<b>Thực đơn hôm nay ({_menuToday.Count}/{max})</b>"
            : $"<b>Thực đơn hôm nay ({_menuToday.Count})</b>");
        y += Row;

        if (_menuToday.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, Row), "<i>Thực đơn đang trống.</i>");
            return y + Row + Pad;
        }

        const float arrowW = 26f;
        const float delW = 26f;
        const float gap = 4f;
        float nameW = w - 2f * arrowW - delW - 3f * gap;

        for (int i = 0; i < _menuToday.Count; i++)
        {
            float cx = x;

            GUI.enabled = i > 0;
            if (GUI.Button(new Rect(cx, y, arrowW, Row), "▲")) { _menuMoveFrom = i; _menuMoveDelta = -1; }
            cx += arrowW + gap;

            GUI.enabled = i < _menuToday.Count - 1;
            if (GUI.Button(new Rect(cx, y, arrowW, Row), "▼")) { _menuMoveFrom = i; _menuMoveDelta = 1; }
            cx += arrowW + gap;

            GUI.enabled = true;
            GUI.Label(new Rect(cx, y, nameW, Row), $"{i + 1}. {MenuTweaks.DisplayName(_menuToday[i])}");
            cx += nameW + gap;

            if (GUI.Button(new Rect(cx, y, delW, Row), "X")) _menuRemove = _menuToday[i];

            y += Row;
        }

        return y + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawQuickAdd(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Thêm nhanh</b>");
        y += Row;

        float mbw = (w - Pad) / 2f;
        if (GUI.Button(new Rect(x, y, mbw, Row), "Thêm món ngẫu nhiên"))
        {
            MenuTweaks.AddRandomDish();
            _menuDirty = true;
        }
        if (GUI.Button(new Rect(x + mbw + Pad, y, mbw, Row), "Xóa thực đơn hôm nay"))
        {
            MenuTweaks.ClearMenu();
            _menuDirty = true;
        }
        y += Row + 4f;

        // Category buttons, laid out to match the tablet's own tabs. These roll a random
        // dish; the picker below is for choosing one by name.
        float cw = (w - (CategoriesPerRow - 1) * 4f) / CategoriesPerRow;

        for (int i = 0; i < MenuTweaks.Categories.Length; i++)
        {
            int col = i % CategoriesPerRow;
            int row = i / CategoriesPerRow;
            var r = new Rect(x + col * (cw + 4f), y + row * (Row + 2f), cw, Row);

            var tag = MenuTweaks.Categories[i];
            if (GUI.Button(r, MenuTweaks.Label(tag)))
            {
                MenuTweaks.AddRandomDish(tag);
                _menuDirty = true;
            }
        }

        return y + CategoryRows() * (Row + 2f) + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawDishPicker(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Chọn món để thêm</b>");
        y += Row;

        // Category cycler. A row of toggle buttons would not fit alongside the quick-add
        // strip without halving the width available to dish names.
        const float navW = 30f;
        int last = MenuTweaks.Categories.Length - 1;

        if (GUI.Button(new Rect(x, y, navW, Row), "◀"))
        {
            _menuCategory = _menuCategory <= -1 ? last : _menuCategory - 1;
            _menuPage = 0;
            _menuDirty = true;
        }

        GUI.Label(new Rect(x + navW + 6f, y, w - 2f * navW - 12f, Row),
                  $"{SelectedCategoryName()} — {_menuAddable.Count} món");

        if (GUI.Button(new Rect(x + w - navW, y, navW, Row), "▶"))
        {
            _menuCategory = _menuCategory >= last ? -1 : _menuCategory + 1;
            _menuPage = 0;
            _menuDirty = true;
        }
        y += Row;

        // Cycles rather than offering three buttons: the order is one choice, and a row of
        // buttons here would eat the width the dish names need.
        if (GUI.Button(new Rect(x, y, w, Row), $"Sắp xếp: {SortName(SelectedSort())}"))
        {
            Plugin.MenuSortMode.Value = (Plugin.MenuSortMode.Value + 1) % 3;
            _menuPage = 0;
            _menuDirty = true;
        }
        y += Row;

        int pages = Mathf.Max(1, (_menuAddable.Count + MenuPageRows - 1) / MenuPageRows);
        _menuPage = Mathf.Clamp(_menuPage, 0, pages - 1);

        if (_menuAddable.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, Row), "<i>Không còn món nào để thêm.</i>");
            y += Row;
        }
        else
        {
            const float addW = 70f;
            const float priceW = 56f;
            int start = _menuPage * MenuPageRows;
            int end = Mathf.Min(_menuAddable.Count, start + MenuPageRows);

            for (int i = start; i < end; i++)
            {
                var r = _menuAddable[i];
                GUI.Label(new Rect(x, y, w - addW - priceW - 8f, Row), PickerLabel(r));
                GUI.Label(new Rect(x + w - addW - priceW - 4f, y, priceW, Row),
                          $"<color=#c8c8c8>{MenuTweaks.PriceText(r)}</color>");
                if (GUI.Button(new Rect(x + w - addW, y, addW, Row), "Thêm")) _menuAdd = r;
                y += Row;
            }

            // Keep the block a fixed height so the panel does not jump on the last page.
            // The same expression measures it in ContentHeight; the two must agree or the
            // page navigation ends up drawn outside the panel.
            y += (Mathf.Min(MenuPageRows, _menuAddable.Count) - (end - start)) * Row;
        }

        float navBw = (w - 2f * Pad) / 3f;

        GUI.enabled = _menuPage > 0;
        if (GUI.Button(new Rect(x, y, navBw, Row), "◀ Trang trước")) _menuPage--;

        GUI.enabled = true;
        GUI.Label(new Rect(x + navBw + Pad, y, navBw, Row), $"  Trang {_menuPage + 1}/{pages}");

        GUI.enabled = _menuPage < pages - 1;
        if (GUI.Button(new Rect(x + 2f * (navBw + Pad), y, navBw, Row), "Trang sau ▶")) _menuPage++;
        GUI.enabled = true;

        return y + Row;
    }

    /// <summary>
    /// Settings for the corner overlay. It lives in this tab because it is about today's
    /// menu; the reading itself is DishStatus's job.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawDishHudSection(float x, float y, float w)
    {
        y += Pad;

        GUI.Label(new Rect(x, y, w, Row), "<b>Bảng theo dõi món</b>");
        y += Row;

        bool on = GUI.Toggle(new Rect(x, y, w, Row), Plugin.DishHudEnabled.Value,
                             "  Hiện số suất còn lại của từng món trên màn hình");
        if (on != Plugin.DishHudEnabled.Value) Plugin.DishHudEnabled.Value = on;
        y += Row;

        GUI.enabled = Plugin.DishHudEnabled.Value;

        GUI.Label(new Rect(x, y, w - 140f - Pad, Row),
                  "<i>Kéo thả bảng khi đang mở bảng này để đổi vị trí.</i>");

        if (GUI.Button(new Rect(x + w - 140f, y, 140f, Row), "Về góc trên phải"))
        {
            // -1 rather than the coordinate of the corner, so the reset still lands
            // correctly if the resolution changes later.
            Plugin.DishHudX.Value = -1f;
            Plugin.DishHudY.Value = -1f;
            _hudPos = new Vector2(float.NaN, float.NaN);
        }
        y += Row;

        y = DrawHudDishFilter(x, y, w);

        GUI.enabled = true;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{DishStatus.Status}</i>");
        return y + Row;
    }

    /// <summary>
    /// Which of today's dishes the overlay lists.
    ///
    /// Every dish on the menu is drawn, ticked or not, so unticking one is undoable from the
    /// same place - a list of only the shown dishes would hide the control for putting a dish
    /// back. The tick colours match the overlay, so a row here reads as the row it governs.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawHudDishFilter(float x, float y, float w)
    {
        var entries = DishStatus.Entries;
        if (entries.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, Row), "<i>Chưa có món nào trong thực đơn hôm nay.</i>");
            return y + Row;
        }

        GUI.Label(new Rect(x, y, w - 110f - Pad, Row), "Chọn món muốn hiện:");

        if (GUI.Button(new Rect(x + w - 110f, y, 110f, Row), "Hiện tất cả"))
            DishStatus.ShowAll();
        y += Row;

        var previous = GUI.contentColor;
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (string.IsNullOrEmpty(e.Name)) continue;

                GUI.contentColor = e.Hidden ? TextBright : HudColour(e.Level);

                bool show = !e.Hidden;
                bool want = GUI.Toggle(new Rect(x + Pad, y, w - Pad, Row), show,
                                       $"  {e.Name}");

                // Write through DishStatus rather than the entry: the list is rebuilt from
                // the counters every few seconds, so a flag set on this object would be gone
                // by the next reading.
                if (want != show) DishStatus.SetHidden(e.Name, !want);
                y += Row;
            }
        }
        finally
        {
            GUI.contentColor = previous;
        }

        return y;
    }

    [HideFromIl2Cpp]
    private void ApplyMenuEdits()
    {
        ApplyPresetActions();

        if (_menuRemove != null)
        {
            MenuTweaks.RemoveDish(_menuRemove);
            _menuRemove = null;
            _menuDirty = true;
        }

        if (_menuAdd != null)
        {
            MenuTweaks.AddDish(_menuAdd);
            _menuAdd = null;
            _menuDirty = true;
        }

        if (_menuMoveFrom >= 0)
        {
            MenuTweaks.Move(_menuMoveFrom, _menuMoveDelta);
            _menuMoveFrom = -1;
            _menuDirty = true;
        }

        if (!_menuDirty) return;
        _menuDirty = false;
        RefreshMenuCache(true);

        // The overlay is driven by counter events, which an edit to the menu is not, so
        // it has to be told or a dish just added would not appear until the next resync.
        DishStatus.Invalidate();
    }

    // ------------------------------------------------------- saved menus

    // Deferred like every other menu edit: applying a preset clears and refills the menu, and
    // doing that halfway through a draw would leave the rows below it describing a menu that
    // no longer exists.
    private bool _presetSave;
    private MenuPresets.Preset _presetApply;
    private MenuPresets.Preset _presetOverwrite;
    private MenuPresets.Preset _presetDelete;

    [HideFromIl2Cpp]
    private void ApplyPresetActions()
    {
        if (_presetSave)
        {
            _presetSave = false;
            MenuPresets.SaveCurrent();
        }

        if (_presetOverwrite != null)
        {
            MenuPresets.Overwrite(_presetOverwrite);
            _presetOverwrite = null;
        }

        if (_presetDelete != null)
        {
            MenuPresets.Delete(_presetDelete);
            _presetDelete = null;
        }

        if (_presetApply != null)
        {
            MenuPresets.Apply(_presetApply);
            _presetApply = null;
            _menuDirty = true;
        }
    }

    /// <summary>
    /// Saved menus: today's list kept for tomorrow.
    ///
    /// Presets are named for you rather than typed. Text entry in this panel is a hand-rolled
    /// digits-only field - the game's GUI.TextField throws here - so a name would have to be
    /// spelled out a keystroke at a time, and the row already says what the preset holds,
    /// which is what tells one apart from another anyway.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawPresetSection(float x, float y, float w)
    {
        var presets = MenuPresets.All();

        GUI.Label(new Rect(x, y, w - 170f - Pad, Row), "<b>Thực đơn đã lưu</b>");

        GUI.enabled = MenuPresets.CanSaveMore();
        if (GUI.Button(new Rect(x + w - 170f, y, 170f, Row), "Lưu thực đơn hôm nay"))
            _presetSave = true;
        GUI.enabled = true;
        y += Row;

        if (presets.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, Row),
                      "<i>Chưa lưu thực đơn nào. Lưu để hôm sau dùng lại ngay.</i>");
            y += Row;
        }
        else
        {
            const float useW = 52f;
            const float saveW = 52f;
            const float delW = 26f;
            const float gap = 4f;
            float nameW = w - useW - saveW - delW - 3f * gap;

            foreach (var preset in presets)
            {
                float cx = x;

                GUI.Label(new Rect(cx, y, nameW, Row),
                          $"{preset.Name}  <color=#b0b0b0>{MenuPresets.Describe(preset)}</color>");
                cx += nameW + gap;

                if (GUI.Button(new Rect(cx, y, useW, Row), "Dùng")) _presetApply = preset;
                cx += useW + gap;

                if (GUI.Button(new Rect(cx, y, saveW, Row), "Ghi đè")) _presetOverwrite = preset;
                cx += saveW + gap;

                if (GUI.Button(new Rect(cx, y, delW, Row), "X")) _presetDelete = preset;

                y += Row;
            }
        }

        GUI.Label(new Rect(x, y, w, Row),
                  $"<i>{MenuPresets.Status} - \"Dùng\" thay toàn bộ thực đơn hôm nay.</i>");
        return y + Row + Pad;
    }

    // ------------------------------------------ interaction sections

    [HideFromIl2Cpp]
    private float DrawShoppingSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Mua sắm</b>");
        y += Row;

        bool on = GUI.Toggle(new Rect(x, y, w, Row), Plugin.StoreHoldRepeat.Value,
                             "  Giữ chuột ở kệ hàng để thêm / bớt liên tục");
        if (on != Plugin.StoreHoldRepeat.Value) Plugin.StoreHoldRepeat.Value = on;
        y += Row;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{StoreRepeater.Status} - nhấn một lần vẫn chỉ thêm một.</i>");
        return y + Row + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawSeasoningSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Gia vị</b>");
        y += Row;

        bool sp = GUI.Toggle(new Rect(x, y, w, Row), Plugin.SpiceTweaksEnabled.Value,
                             "  Bật tinh chỉnh gia vị");
        if (sp != Plugin.SpiceTweaksEnabled.Value)
        {
            Plugin.SpiceTweaksEnabled.Value = sp;
            if (!sp) SpiceTweaks.Restore(_interact);
        }
        y += Row;

        GUI.enabled = Plugin.SpiceTweaksEnabled.Value;

        bool inst = GUI.Toggle(new Rect(x, y, w, Row), Plugin.SpiceInstant.Value,
                               "  Bỏ độ trễ khi giữ");
        if (inst != Plugin.SpiceInstant.Value)
        {
            Plugin.SpiceInstant.Value = inst;
            if (!inst) SpiceTweaks.Restore(_interact);
        }
        y += Row;

        y = IntRow(x, y, w, "Số gam mỗi lần rắc", Plugin.SpiceAmount, 1);
        GUI.enabled = true;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{SpiceTweaks.Status}</i>");
        return y + Row + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawFishSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Cắt cá</b>");
        y += Row;

        bool fish = GUI.Toggle(new Rect(x, y, w, Row), Plugin.FishCutSkip.Value,
                               "  Bỏ qua minigame cắt cá");
        if (fish != Plugin.FishCutSkip.Value) Plugin.FishCutSkip.Value = fish;
        y += Row;

        GUI.Label(new Rect(x, y, w, Row),
                  $"<i>{FishCutting.Status} - cá được cắt như khi chơi không trúng gai.</i>");
        return y + Row + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawSlicerSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Máy thái</b>");
        y += Row;

        bool any = GUI.Toggle(new Rect(x, y, w, Row), Plugin.SlicerAnyItem.Value,
                              "  Nhận mọi nguyên liệu thái được, không chỉ rau củ");
        if (any != Plugin.SlicerAnyItem.Value) Plugin.SlicerAnyItem.Value = any;
        y += Row;

        bool feed = GUI.Toggle(new Rect(x, y, w, Row), Plugin.SlicerLoadArmful.Value,
                               "  Shift + chuột trái vào máy: đưa cả ôm đồ vào thái");
        if (feed != Plugin.SlicerLoadArmful.Value) Plugin.SlicerLoadArmful.Value = feed;
        y += Row;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{SlicerTweaks.Status}.</i>");
        return y + Row + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawBoxSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Thùng hàng</b>");
        y += Row;

        bool box = GUI.Toggle(new Rect(x, y, w, Row), Plugin.BoxPutBack.Value,
                              "  Chuột phải để bỏ đồ vào lại");
        if (box != Plugin.BoxPutBack.Value) Plugin.BoxPutBack.Value = box;
        y += Row;

        // Above the GUI.enabled gate below on purpose: this is its own option now, so it
        // has to stay usable with put-back switched off.
        bool keep = GUI.Toggle(new Rect(x, y, w, Row), Plugin.BoxKeepEmpty.Value,
                               "  Thùng rỗng không mất");
        if (keep != Plugin.BoxKeepEmpty.Value) Plugin.BoxKeepEmpty.Value = keep;
        y += Row;

        GUI.enabled = Plugin.BoxPutBack.Value;

        // The box repeat used to share the shopping section's toggle, so switching that
        // off silently stopped the box working with nothing here to say why.
        bool rep = GUI.Toggle(new Rect(x, y, w, Row), Plugin.BoxHoldRepeat.Value,
                              "  Giữ để lặp (bỏ vào và lấy ra liên tục)");
        if (rep != Plugin.BoxHoldRepeat.Value) Plugin.BoxHoldRepeat.Value = rep;
        y += Row;

        GUI.Label(new Rect(x, y, LabelW + 60f, Row), "Phím lấy đồ ra (giữ để lặp)");

        bool armingTake = _bindingFor == TakeKeySlot;
        var prevTakeBg = GUI.backgroundColor;
        if (armingTake) GUI.backgroundColor = new Color(1f, 0.8f, 0.3f, prevTakeBg.a);

        if (GUI.Button(new Rect(x + LabelW + 65f, y, 100f, Row),
                       armingTake ? "nhấn phím" : Plugin.BoxTakeOutKey.Value))
        {
            _bindingFor = armingTake ? null : TakeKeySlot;
            _status = armingTake ? "Đã hủy gán phím." : "Nhấn một phím để gán, Esc để hủy.";
        }
        GUI.backgroundColor = prevTakeBg;
        y += Row;

        GUI.Label(new Rect(x, y, LabelW + 60f, Row), "Giữ để hủy cả chồng đồ");

        bool arming = _bindingFor == BoxKeySlot;
        var prevBg = GUI.backgroundColor;
        if (arming) GUI.backgroundColor = new Color(1f, 0.8f, 0.3f, prevBg.a);

        if (GUI.Button(new Rect(x + LabelW + 65f, y, 100f, Row),
                       arming ? "nhấn phím" : Plugin.BoxDestroyKey.Value))
        {
            _bindingFor = arming ? null : BoxKeySlot;
            _status = arming ? "Đã hủy gán phím." : "Nhấn một phím để gán, Esc để hủy.";
        }
        GUI.backgroundColor = prevBg;
        GUI.enabled = true;
        y += Row;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{BoxTweaks.Status}</i>");
        return y + Row + Pad;
    }

    /// <summary>The configured modifier, normalised to one of the four names offered.</summary>
    [HideFromIl2Cpp]
    private static string ModifierLabel()
    {
        switch ((Plugin.PlaceAllModifier.Value ?? "Shift").Trim().ToLowerInvariant())
        {
            case "none": return "None";
            case "ctrl":
            case "control": return "Ctrl";
            case "alt": return "Alt";
            default: return "Shift";
        }
    }

    [HideFromIl2Cpp]
    private float DrawServingSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Quầy phục vụ</b>");
        y += Row;

        bool top = GUI.Toggle(new Rect(x, y, w, Row), Plugin.CounterTopUpEnabled.Value,
                              "  Cộng dồn suất khi phục vụ vào quầy còn đồ");
        if (top != Plugin.CounterTopUpEnabled.Value) Plugin.CounterTopUpEnabled.Value = top;
        y += Row;

        GUI.Label(new Rect(x, y, w, Row),
                  "<i>Mỗi đĩa cộng thêm đúng sức chứa gốc của quầy (4, 5 hay 6); mỗi quầy một món.</i>");
        y += Row;

        // Says so before they try it: the order plates are served in is now the order they
        // come back out in, which is not something the base game ever asks them to think about.
        GUI.Label(new Rect(x, y, w, Row),
                  "<i>Mỗi đĩa giữ chất lượng riêng; đĩa phục vụ sau nằm trên và được lấy ra trước.</i>");
        y += Row;

        // The remove key is the game's own, so say what it now does before they press it and
        // find out. Only ever on a counter this mod has stacked - an ordinary counter is
        // cleared exactly as it always was.
        GUI.Label(new Rect(x, y, w, Row),
                  CounterTopUp.Unstacks
                      ? "<i>Bỏ món khi quầy đang cộng dồn: mỗi lần bớt một đĩa và trả lại một đĩa.</i>"
                      : "<i>Chưa móc được nút bỏ món - bỏ món sẽ dọn sạch cả chồng.</i>");
        y += Row;

        GUI.Label(new Rect(x, y, w, Row),
                  "<i>Chơi mạng: cả hai máy đều phải bật mục này thì số suất mới khớp.</i>");
        y += Row;

        GUI.enabled = Plugin.CounterTopUpEnabled.Value && CounterTopUp.CanResize;
        y = IntRow(x, y, w, "Trần số suất (0 = không giới hạn)", Plugin.CounterStackLimit, 0, 99);
        GUI.enabled = true;

        string state = !CounterTopUp.Patched ? "chưa gắn được vào game - xem log"
                     : !Plugin.CounterTopUpEnabled.Value ? "đã tắt"
                     : !CounterTopUp.StacksOnClick ? "thiếu móc nút phục vụ - xem log"
                     : CounterTopUp.Status;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{state}</i>");
        return y + Row + Pad;
    }

    [HideFromIl2Cpp]
    private float DrawPlatingSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Bày đĩa</b>");
        y += Row;

        // Deliberately above the GUI.enabled gate below: this does not use the placement
        // grid at all, it repeats the game's own plating action.
        bool all = GUI.Toggle(new Rect(x, y, LabelW + 60f, Row), Plugin.PlaceAllEnabled.Value,
                              $"  {ModifierLabel()} + chuột trái: đặt hết đồ lên đĩa/khay");
        if (all != Plugin.PlaceAllEnabled.Value) Plugin.PlaceAllEnabled.Value = all;

        // A plain cycle rather than a key-capture button: modifiers are deliberately kept out
        // of Teleporter.BindableKeys, since they cannot be read as a plain press.
        if (GUI.Button(new Rect(x + LabelW + 65f, y, 100f, Row), ModifierLabel()))
        {
            string[] order = { "Shift", "Ctrl", "Alt", "None" };
            int at = Array.IndexOf(order, ModifierLabel());
            Plugin.PlaceAllModifier.Value = order[(at + 1) % order.Length];
            Plugin.PlaceAllModifier.ConfigFile.Save();
        }
        y += Row;

        bool auto = GUI.Toggle(new Rect(x, y, w, Row), Plugin.PlateAutoAlign.Value,
                               "  Canh vị trí khi đặt đồ (trừ chế độ xây dựng)");
        if (auto != Plugin.PlateAutoAlign.Value) Plugin.PlateAutoAlign.Value = auto;
        y += Row;

        GUI.enabled = Plugin.PlateAutoAlign.Value;

        bool mode = GUI.Toggle(new Rect(x, y, LabelW + 60f, Row), Plugin.PlateEdgeSnap.Value,
                               Plugin.PlateEdgeSnap.Value ? "  Chế độ ghép cạnh" : "  Chế độ lưới");
        if (mode != Plugin.PlateEdgeSnap.Value) Plugin.PlateEdgeSnap.Value = mode;

        bool armingSnap = _bindingFor == SnapKeySlot;
        var prevSnapBg = GUI.backgroundColor;
        if (armingSnap) GUI.backgroundColor = new Color(1f, 0.8f, 0.3f, prevSnapBg.a);

        if (GUI.Button(new Rect(x + LabelW + 65f, y, 100f, Row),
                       armingSnap ? "nhấn phím" : Plugin.PlateSnapToggleKey.Value))
        {
            _bindingFor = armingSnap ? null : SnapKeySlot;
            _status = armingSnap ? "Đã hủy gán phím." : "Nhấn một phím để gán, Esc để hủy.";
        }
        GUI.backgroundColor = prevSnapBg;
        y += Row;

        y = NumberRow(x, y, w, "Bước lưới (m)", Plugin.PlateGridSize, 0.005f);
        GUI.enabled = true;

        if (GUI.Button(new Rect(x, y, LabelW + 60f, Row), "Canh thẳng đồ trên đĩa"))
            PlateTweaks.Align(_interact);

        bool armingPlate = _bindingFor == PlateKeySlot;
        var prevPlateBg = GUI.backgroundColor;
        if (armingPlate) GUI.backgroundColor = new Color(1f, 0.8f, 0.3f, prevPlateBg.a);

        if (GUI.Button(new Rect(x + LabelW + 65f, y, 100f, Row),
                       armingPlate ? "nhấn phím" : Plugin.PlateAlignKey.Value))
        {
            _bindingFor = armingPlate ? null : PlateKeySlot;
            _status = armingPlate ? "Đã hủy gán phím." : "Nhấn một phím để gán, Esc để hủy.";
        }
        GUI.backgroundColor = prevPlateBg;
        y += Row;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{PlateTweaks.Status}</i>");
        y += Row;

        if (Plugin.PlaceAllEnabled.Value)
        {
            GUI.Label(new Rect(x, y, w, Row), $"<i>Đặt hết: {PlaceAll.Status}</i>");
            y += Row;
        }

        return DrawPlateStockSection(x, y + Pad, w);
    }

    /// <summary>
    /// Which plate cabinets waiters may draw from.
    ///
    /// Every cabinet is listed with its contents, and the one nearest the player is marked, so
    /// the row can be matched to the cabinet in front of you without walking away to check.
    /// </summary>
    [HideFromIl2Cpp]
    private float DrawPlateStockSection(float x, float y, float w)
    {
        GUI.Label(new Rect(x, y, w, Row), "<b>Tủ đĩa</b>");
        y += Row;

        bool menu = GUI.Toggle(new Rect(x, y, w, Row), Plugin.PlateStockMenuEnabled.Value,
                               "  Hiện bảng nổi khi nhìn vào tủ đĩa");
        if (menu != Plugin.PlateStockMenuEnabled.Value) Plugin.PlateStockMenuEnabled.Value = menu;
        y += Row;

        GUI.Label(new Rect(x, y, w - 110f - Pad, Row),
                  $"Phím bật/tắt: {Plugin.PlateStockToggleKey.Value}");

        if (GUI.Button(new Rect(x + w - 110f, y, 110f, Row), "Bỏ loại trừ"))
            PlateStocks.IncludeAll();
        y += Row;

        GUI.Label(new Rect(x, y, w, Row), $"<i>{PlateStocks.Status}</i>");
        return y + Row;
    }

    /// <summary>Width of the floating cabinet menu.</summary>
    private const float StockMenuWidth = 230f;

    /// <summary>
    /// The floating menu over a plate cabinet you are looking at.
    ///
    /// Drawn at the cabinet rather than in a corner so there is no question which one it
    /// governs - the objection to the list was exactly that a row could not be matched to a
    /// cabinet. It is display only: the cursor is locked to the camera in play, so the toggle
    /// is a key press rather than something to click.
    /// </summary>
    [HideFromIl2Cpp]
    private void DrawStockMenu()
    {
        if (!Plugin.PlateStockMenuEnabled.Value) return;

        var stock = PlateStocks.Aimed;
        if (stock == null) return;

        string title;
        bool excluded;
        try
        {
            title = PlateStocks.Label(stock);
            excluded = PlateStocks.IsExcluded(stock);
        }
        catch { return; }

        float h = Pad + 3f * HudRow + Pad;
        var p = PlateStocks.AimedScreenPos;

        // Clamped so a cabinet near the screen edge still shows the whole menu.
        var r = new Rect(
            Mathf.Clamp(p.x - StockMenuWidth * 0.5f, Pad, Screen.width - StockMenuWidth - Pad),
            Mathf.Clamp(p.y - h - 10f, Pad, Screen.height - h - Pad),
            StockMenuWidth, h);

        Backdrop(r);

        float ix = r.x + Pad;
        float iw = StockMenuWidth - 2f * Pad;
        float iy = r.y + Pad;

        GUI.Label(new Rect(ix, iy, iw, HudRow), $"<b>{title}</b>");
        iy += HudRow;

        var previous = GUI.contentColor;
        try
        {
            GUI.contentColor = excluded ? new Color(1f, 0.44f, 0.40f) : new Color(0.48f, 1f, 0.52f);
            GUI.Label(new Rect(ix, iy, iw, HudRow),
                      excluded ? "Khách KHÔNG lấy đĩa ở đây" : "Khách được lấy đĩa ở đây");
        }
        finally { GUI.contentColor = previous; }
        iy += HudRow;

        // On a client the key press is a request to the host, and saying so is the difference
        // between "nothing happened" reading as a broken mod or as a message still in flight.
        string hint = $"Nhấn {Plugin.PlateStockToggleKey.Value} để đổi";
        if (PlateStocksNet.FollowsHost)
            hint = PlateStocksNet.RequestPending
                ? "Đang gửi yêu cầu tới chủ phòng..."
                : PlateStocksNet.RequestFailed
                    ? "Chủ phòng không trả lời yêu cầu"
                    : PlateStocksNet.RemoteKnown
                        ? $"Nhấn {Plugin.PlateStockToggleKey.Value} để xin chủ phòng đổi"
                        : PlateStocksNet.HostSilent
                            ? "Chủ phòng chưa trả lời - có thể chưa cài mod"
                            : "Đang chờ chủ phòng...";

        GUI.Label(new Rect(ix, iy, iw, HudRow), $"<size=10>{hint}</size>");
    }

    // ---------------------------------------------------------- teleport tab

    [HideFromIl2Cpp]
    private float DrawTeleportTab(float x, float y, float w)
    {
        if (_fpc == null)
        {
            GUI.Label(new Rect(x, y, w, Row), "Không tìm thấy người chơi.");
            GUI.Label(new Rect(x, y + Row, w, Row), "Hãy vào màn chơi trước đã.");
            return y + 2f * Row;
        }

        GUI.Label(new Rect(x, y, w, Row), _status ?? $"Đã tìm thấy {_locations.Count} địa điểm.");
        y += Row;

        if (_locations.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, Row), "Không tìm thấy gì - hãy bấm Quét lại sau khi vào màn.");
            y += Row;
        }

        const float bindW = 86f;
        const float goW = 46f;
        const float delW = 24f;
        const float gap = 4f;

        foreach (var loc in _locations)
        {
            float dist = Vector3.Distance(_fpc.transform.position, loc.Position);
            string tag = loc.Custom ? "★ " : "";

            float labelW = w - bindW - goW - delW - 3f * gap;
            GUI.Label(new Rect(x, y, labelW, Row), $"{tag}{loc.Name}   <i>{dist:0}m</i>");

            // Hotkey button: shows the bound key, or invites a binding.
            float bx = x + labelW + gap;
            bool arming = _bindingFor == loc.Name;
            string keyLabel = arming
                ? "nhấn phím"
                : _bindings.TryGetValue(loc.Name, out var bound) ? bound.ToString() : "gán";

            var prevBg = GUI.backgroundColor;
            if (arming) GUI.backgroundColor = new Color(1f, 0.8f, 0.3f, prevBg.a);
            if (GUI.Button(new Rect(bx, y, bindW, Row), keyLabel))
            {
                _bindingFor = arming ? null : loc.Name;
                _status = arming
                    ? "Đã hủy gán phím."
                    : "Nhấn một phím để gán, Esc để hủy, Backspace để xóa.";
            }
            GUI.backgroundColor = prevBg;

            if (GUI.Button(new Rect(bx + bindW + gap, y, goW, Row), "Đi"))
                Jump(loc);

            if (loc.Custom && GUI.Button(new Rect(bx + bindW + goW + 2f * gap, y, delW, Row), "X"))
                _pendingDelete = loc;

            y += Row;
        }

        // Mutating the list mid-draw would break IMGUI's layout, so deletes are deferred.
        if (_pendingDelete != null)
        {
            _custom.Remove(_pendingDelete);
            // Drop the hotkey too, or it would point at a location that no longer exists.
            if (_bindings.Remove(_pendingDelete.Name)) PersistBindings();
            if (_bindingFor == _pendingDelete.Name) _bindingFor = null;
            PersistWaypoints();
            _pendingDelete = null;
            RefreshLocations();
        }

        y += Pad;

        bool hk = GUI.Toggle(new Rect(x, y, w, Row), Plugin.HotkeysEnabled.Value,
                             "  Phím tắt dịch chuyển vẫn chạy khi đóng bảng");
        if (hk != Plugin.HotkeysEnabled.Value) Plugin.HotkeysEnabled.Value = hk;
        y += Row;

        float bw = (w - 2f * Pad) / 3f;
        if (GUI.Button(new Rect(x, y, bw, Row), "Quét lại"))
            RefreshLocations();

        if (GUI.Button(new Rect(x + bw + Pad, y, bw, Row), "Lưu vị trí"))
            SaveCurrentSpot();

        GUI.enabled = _hasReturn;
        if (GUI.Button(new Rect(x + 2f * (bw + Pad), y, bw, Row), "Hoàn tác"))
            Jump(_returnPos, "vị trí trước đó", record: false);
        GUI.enabled = true;

        y += Row + Pad;
        var p = _fpc.transform.position;
        GUI.Label(new Rect(x, y, w, Row), $"<i>Bạn: {p.x:0.0}, {p.y:0.0}, {p.z:0.0}</i>");
        return y + Row;
    }

    /// <summary>
    /// Travels to a location, preferring the game's own transition where one exists.
    ///
    /// The home door loads and unloads the house model as you pass through it; moving the
    /// player there directly skipped that, leaving the house missing or still rendered.
    /// </summary>
    [HideFromIl2Cpp]
    private void Jump(Teleporter.Location loc)
    {
        if (loc == null) return;

        // Only a door transition runs on in the background after we teleport, and only
        // that case needs the destination held afterwards.
        bool transitionRunning = false;

        if (loc.Door != null)
        {
            // Record the return point ourselves: the game's transition moves the player,
            // so Undo still has somewhere to go back to.
            _returnPos = _fpc != null ? _fpc.transform.position : _returnPos;
            _hasReturn = _fpc != null;

            if (Teleporter.TryUseDoor(loc, out string doorError))
            {
                _status = $"Đã đi tới {loc.Name} qua cửa.";
                return;
            }

            _status = $"Không đi qua cửa được ({doorError}); đã dịch chuyển thẳng.";
            transitionRunning = true;
        }
        else if (Teleporter.TryExitHome(_fpc))
        {
            // Leaving the house has to go through the interior door, or the exterior model
            // is never restored. The transition then continues in the background while we
            // teleport straight away - no waiting.
            _status = $"Đang rời khỏi nhà, đi tới {loc.Name}.";
            transitionRunning = true;
        }

        Jump(loc.Position, loc.Name, record: true, holdDestination: transitionRunning);
    }

    // The house transition repositions the player as part of stepping through the door.
    // Rather than delaying the jump until it finishes, we teleport immediately and hold
    // the destination for a moment, undoing any late move the transition makes.
    //
    // Three things keep the hold from turning into the "stuck after teleporting" bug it
    // used to be. It is armed only when a door transition is actually in flight - an
    // ordinary jump has nothing to undo. It gives up the instant the player presses a
    // movement key, because a player walking away is not a transition dragging them off.
    // And it corrects at most once, so it can never sit there yanking them back for the
    // full two seconds.
    private const float HoldSeconds = 2f;
    private const float HoldSnapBack = 2f;   // metres of drift that counts as a yank

    private Vector3 _holdTarget;
    private float _holdUntil;
    private bool _holding;

    [HideFromIl2Cpp]
    private static bool MovementKeyHeld()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            return kb[Key.W].isPressed || kb[Key.A].isPressed ||
                   kb[Key.S].isPressed || kb[Key.D].isPressed ||
                   kb[Key.UpArrow].isPressed || kb[Key.DownArrow].isPressed ||
                   kb[Key.LeftArrow].isPressed || kb[Key.RightArrow].isPressed ||
                   kb[Key.Space].isPressed;
        }
        catch { return false; }
    }

    [HideFromIl2Cpp]
    private void TickDestinationHold()
    {
        if (!_holding || _fpc == null) return;

        if (Time.unscaledTime > _holdUntil) { _holding = false; return; }

        // The player moving under their own steam ends the hold. Without this the hold
        // fought them: two metres of running was enough to look like a yank, so they were
        // dragged back to the arrival point over and over until the timer ran out.
        if (MovementKeyHeld())
        {
            _holding = false;
            return;
        }

        // Only correct a real displacement. A small threshold would fight the player's
        // own movement and pin them in place.
        if (Vector3.Distance(_fpc.transform.position, _holdTarget) < HoldSnapBack) return;

        // One correction, then done. The transition only moves you once.
        _holding = false;

        if (Teleporter.TeleportTo(_fpc, _holdTarget, out _))
            Plugin.Logger.LogInfo("Teleport: re-applied destination after the house transition moved us.");
    }

    [HideFromIl2Cpp]
    private void Jump(Vector3 target, string label, bool record = true, bool holdDestination = false)
    {
        bool wasHasReturn = _hasReturn;
        if (record)
        {
            _returnPos = _fpc.transform.position;
            _hasReturn = true;
        }

        if (Teleporter.TeleportTo(_fpc, target, out string error))
        {
            _status = $"Đã dịch chuyển tới {label}.";

            // Arm the watchdog against a landing the ground probe got wrong.
            _landedAt = _fpc.transform.position;
            _watchFallUntil = Time.time + FallWatchSeconds;
            _watchingFall = record;

            // Hold the destination briefly, but only when a door transition is still
            // running in the background and could drag us off it.
            _holding = holdDestination;
            if (holdDestination)
            {
                _holdTarget = _fpc.transform.position;
                _holdUntil = Time.unscaledTime + HoldSeconds;
            }
        }
        else
        {
            // Nothing moved, so the recorded return point is not a jump we can undo.
            if (record) _hasReturn = wasHasReturn;
            _status = $"Dịch chuyển thất bại: {error}";
            Plugin.Logger.LogWarning($"Teleport to {label} failed: {error}");
        }
    }

    [HideFromIl2Cpp]
    private void RefreshLocations()
    {
        _locations.Clear();
        _locations.AddRange(Teleporter.Discover());
        _locations.AddRange(_custom);
        _status = $"Đã tìm thấy {_locations.Count} địa điểm.";
        Plugin.Logger.LogInfo(
            $"Teleport scan: {_locations.Count} locations -> " +
            string.Join(", ", _locations.ConvertAll(l => l.Name)));
    }

    [HideFromIl2Cpp]
    private void SaveCurrentSpot()
    {
        if (_fpc == null) return;

        _custom.Add(new Teleporter.Location
        {
            Name = $"Điểm mốc {_custom.Count + 1}",
            Position = _fpc.transform.position,
            Custom = true,
        });
        PersistWaypoints();
        RefreshLocations();
        _status = "Đã lưu vị trí hiện tại.";
    }

    [HideFromIl2Cpp]
    private void PersistWaypoints()
    {
        Plugin.Waypoints.Value = Teleporter.SaveCustom(_custom);
        Plugin.Waypoints.ConfigFile.Save();
    }


    /// <summary>Whole-number variant of <see cref="NumberRow"/>, for counts.</summary>
    [HideFromIl2Cpp]
    private float IntRow(float x, float y, float w, string label, ConfigEntry<int> cfg,
                         int min, int max = int.MaxValue)
    {
        GUI.Label(new Rect(x, y, LabelW, Row), label);
        ValueBox(new Rect(x + LabelW + 5f, y, FieldW, Row), $"i{label}", null, cfg,
                 0f, 0f, cfg.Value.ToString(), min, max);
        return y + Row;
    }

    /// <summary>
    /// A labelled number you type into. No slider: a slider's track imposes an upper
    /// bound, and these values are worth being able to push past whatever range looked
    /// sensible when the row was written. Only a lower bound is enforced, and only where
    /// a smaller value would break the game rather than merely look silly.
    /// </summary>
    [HideFromIl2Cpp]
    private float NumberRow(float x, float y, float w, string label, ConfigEntry<float> cfg,
                            float min, float max = float.PositiveInfinity)
    {
        GUI.Label(new Rect(x, y, LabelW, Row), label);
        ValueBox(new Rect(x + LabelW + 5f, y, FieldW, Row), $"f{label}", cfg, null,
                 min, max, cfg.Value.ToString("0.##"));
        return y + Row;
    }

    private void HandleDrag()
    {
        var e = Event.current;
        var title = new Rect(_panel.x, _panel.y, _panel.width, 26f);

        if (e.type == EventType.MouseDown && title.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - new Vector2(_panel.x, _panel.y);
            e.Use();
        }
        else if (e.type == EventType.MouseUp)
        {
            _dragging = false;
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            _panel.x = e.mousePosition.x - _dragOffset.x;
            _panel.y = e.mousePosition.y - _dragOffset.y;
            e.Use();
        }
    }

    private void LogOnce(string message)
    {
        if (_loggedOnce.Add(message)) Plugin.Logger.LogWarning(message);
    }
}
