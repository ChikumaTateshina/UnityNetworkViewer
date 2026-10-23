using System;
using System.Collections.Generic;

namespace ObjectRelationshipExplorer.Editor.VRChat
{
    /// <summary>
    /// 操作 API のルールセット。静的解析で見つかった呼出し・代入を「操作カテゴリ」に分類する。
    /// 分類は推定であり、実行時に呼ばれることを意味しない。
    /// </summary>
    public static class OperationRules
    {
        public sealed class Rule
        {
            public string Category;
            public string Api;
            /// <summary>静的呼出しで操作対象が何番目の引数か（-1 はインスタンス）。</summary>
            public int TargetArgument = -1;
            public bool IsDispatch;
            public bool IsNetwork;
            /// <summary>イベント名の引数位置（Dispatch のみ）。</summary>
            public int EventNameArgument = -1;
        }

        public const string Activation = "Activation";
        public const string Transform = "Transform";
        public const string Animator = "Animator";
        public const string Event = "Event";
        public const string ProgramVariable = "ProgramVariable";
        public const string Audio = "Audio";
        public const string Enable = "Enable";
        public const string ObjectSync = "ObjectSync";
        public const string Pickup = "Pickup";
        public const string Ownership = "Ownership";
        public const string Lifecycle = "Lifecycle";
        public const string Particle = "Particle";
        public const string UI = "UI";
        public const string Rendering = "Rendering";

        static readonly Dictionary<string, Rule> ByTypeAndMember = new Dictionary<string, Rule>(StringComparer.Ordinal);
        static readonly Dictionary<string, Rule> ByMemberOnly = new Dictionary<string, Rule>(StringComparer.Ordinal);
        static readonly HashSet<string> AmbiguousMembers = new HashSet<string>(StringComparer.Ordinal);

        static OperationRules()
        {
            Add("GameObject", "SetActive", Activation);

            foreach (var m in new[] { "position", "localPosition", "rotation", "localRotation", "localScale", "eulerAngles", "localEulerAngles", "parent" })
                Add("Transform", "set_" + m, Transform, "Transform." + m + " =");
            foreach (var m in new[] { "SetPositionAndRotation", "SetLocalPositionAndRotation", "Translate", "Rotate", "RotateAround", "LookAt", "SetParent", "SetSiblingIndex", "SetAsFirstSibling", "SetAsLastSibling" })
                Add("Transform", m, Transform);

            foreach (var m in new[] { "SetBool", "SetTrigger", "ResetTrigger", "SetFloat", "SetInteger", "Play", "PlayInFixedTime", "CrossFade", "CrossFadeInFixedTime", "Rebind", "SetLayerWeight" })
                Add("Animator", m, Animator);
            Add("Animator", "set_speed", Animator, "Animator.speed =");
            Add("Animator", "set_runtimeAnimatorController", Animator, "Animator.runtimeAnimatorController =");

            foreach (var t in new[] { "UdonBehaviour", "IUdonEventReceiver", "UdonSharpBehaviour", "AbstractUdonBehaviour" })
            {
                AddDispatch(t, "SendCustomEvent", false, 0);
                AddDispatch(t, "SendCustomEventDelayedSeconds", false, 0);
                AddDispatch(t, "SendCustomEventDelayedFrames", false, 0);
                AddDispatch(t, "SendCustomNetworkEvent", true, 1);
                Add(t, "SetProgramVariable", ProgramVariable);
            }

            foreach (var m in new[] { "Play", "Stop", "Pause", "UnPause", "PlayOneShot", "PlayDelayed", "PlayScheduled" })
                Add("AudioSource", m, Audio);
            foreach (var m in new[] { "clip", "volume", "pitch", "mute", "loop", "time" })
                Add("AudioSource", "set_" + m, Audio, "AudioSource." + m + " =");

            foreach (var t in new[] { "Behaviour", "MonoBehaviour", "Renderer", "MeshRenderer", "SkinnedMeshRenderer", "SpriteRenderer", "Collider", "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "Light", "Camera", "Canvas", "UdonBehaviour", "AudioSource", "Animator", "Rigidbody" })
                Add(t, "set_enabled", Enable, t + ".enabled =");
            Add("Rigidbody", "set_isKinematic", Enable, "Rigidbody.isKinematic =");
            Add("Rigidbody", "set_velocity", Transform, "Rigidbody.velocity =");
            Add("Rigidbody", "AddForce", Transform);
            Add("Rigidbody", "MovePosition", Transform);
            Add("Rigidbody", "MoveRotation", Transform);

            foreach (var m in new[] { "Respawn", "SetKinematic", "SetGravity", "TeleportTo", "FlagDiscontinuity" })
                Add("VRCObjectSync", m, ObjectSync);
            Add("VRCObjectPool", "TryToSpawn", ObjectSync);
            Add("VRCObjectPool", "Return", ObjectSync);
            Add("VRCPickup", "Drop", Pickup);
            Add("VRCPickup", "set_pickupable", Pickup, "VRCPickup.pickupable =");

            foreach (var m in new[] { "Play", "Stop", "Pause", "Clear", "Emit" })
                Add("ParticleSystem", m, Particle);

            foreach (var t in new[] { "Text", "TextMeshPro", "TextMeshProUGUI", "TMP_Text", "InputField", "TMP_InputField" })
                Add(t, "set_text", UI, t + ".text =");
            Add("Slider", "set_value", UI, "Slider.value =");
            Add("Toggle", "set_isOn", UI, "Toggle.isOn =");
            Add("Image", "set_sprite", UI, "Image.sprite =");
            Add("Image", "set_color", UI, "Image.color =");
            foreach (var t in new[] { "Renderer", "MeshRenderer", "SkinnedMeshRenderer" })
            {
                Add(t, "set_material", Rendering, t + ".material =");
                Add(t, "set_sharedMaterial", Rendering, t + ".sharedMaterial =");
                Add(t, "SetPropertyBlock", Rendering);
            }

            // 静的呼出し（引数が対象）
            AddStatic("Networking", "SetOwner", Ownership, 1);
            AddStatic("Object", "Destroy", Lifecycle, 0);
            AddStatic("Object", "DestroyImmediate", Lifecycle, 0);
            AddStatic("Object", "Instantiate", Lifecycle, 0);
            AddStatic("VRCInstantiate", "Instantiate", Lifecycle, 0);
        }

        static void Add(string type, string member, string category, string api = null)
        {
            var rule = new Rule { Category = category, Api = api ?? type + "." + member };
            ByTypeAndMember[type + "." + member] = rule;
            if (ByMemberOnly.TryGetValue(member, out var existing) && existing.Category != category) AmbiguousMembers.Add(member);
            else ByMemberOnly[member] = rule;
        }

        static void AddDispatch(string type, string member, bool network, int eventArg)
        {
            var rule = new Rule { Category = Event, Api = member, IsDispatch = true, IsNetwork = network, EventNameArgument = eventArg };
            ByTypeAndMember[type + "." + member] = rule;
            ByMemberOnly[member] = rule;
        }

        static void AddStatic(string type, string member, string category, int targetArg)
        {
            ByTypeAndMember[type + "." + member] = new Rule { Category = category, Api = type + "." + member, TargetArgument = targetArg };
        }

        /// <summary>型が分かる場合は型＋メンバで、分からない場合はメンバ名のみで分類する（曖昧なら null）。</summary>
        public static Rule Classify(string declaredType, string member)
        {
            if (string.IsNullOrEmpty(member)) return null;
            if (!string.IsNullOrEmpty(declaredType))
            {
                string t = ShortTypeName(declaredType);
                if (ByTypeAndMember.TryGetValue(t + "." + member, out var r)) return r;
                // 派生型（XxxRenderer 等）
                if (t.EndsWith("Renderer", StringComparison.Ordinal) && ByTypeAndMember.TryGetValue("Renderer." + member, out r)) return r;
                if (t.EndsWith("Collider", StringComparison.Ordinal) && ByTypeAndMember.TryGetValue("Collider." + member, out r)) return r;
            }
            if (AmbiguousMembers.Contains(member)) return null;
            return ByMemberOnly.TryGetValue(member, out var m) ? m : null;
        }

        public static Rule ClassifyStatic(string type, string member)
        {
            string t = ShortTypeName(type);
            return ByTypeAndMember.TryGetValue(t + "." + member, out var r) && r.TargetArgument >= 0 ? r : null;
        }

        public static bool IsAmbiguous(string member) => AmbiguousMembers.Contains(member);

        public static string ShortTypeName(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = t.Trim();
            if (t.EndsWith("[]", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 2);
            int lt = t.IndexOf('<');
            if (lt > 0) t = t.Substring(0, lt);
            int dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        /// <summary>Udon の extern 型トークン（"UnityEngineGameObject" 等）を短い型名へ。</summary>
        public static string FromUdonTypeToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return token;
            string[] prefixes =
            {
                "VRCUdonCommonInterfaces", "VRCSDK3ComponentsVideo", "VRCSDK3Components", "VRCSDKBase", "VRCUdon",
                "UnityEngineUI", "UnityEngineAI", "UnityEngine", "TMPro", "System",
            };
            foreach (var p in prefixes)
                if (token.StartsWith(p, StringComparison.Ordinal) && token.Length > p.Length) return token.Substring(p.Length);
            return token;
        }

        /// <summary>イベント名の比較用正規化（"_interact" と "Interact" を同一視）。</summary>
        public static string NormalizeEventName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            return name.TrimStart('_').ToLowerInvariant();
        }

        /// <summary>Udon / UdonSharp が呼び出すエントリポイント（イベント）名。</summary>
        public static readonly HashSet<string> BuiltinEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "Interact",
            "OnPickup", "OnDrop", "OnPickupUseDown", "OnPickupUseUp", "OnPlayerJoined", "OnPlayerLeft",
            "OnPlayerTriggerEnter", "OnPlayerTriggerExit", "OnPlayerTriggerStay", "OnPlayerCollisionEnter", "OnPlayerCollisionExit",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay", "OnCollisionEnter", "OnCollisionExit",
            "OnDeserialization", "OnPreSerialization", "OnPostSerialization", "OnOwnershipTransferred", "OnOwnershipRequest",
            "OnStationEntered", "OnStationExited", "OnVideoStart", "OnVideoEnd", "OnVideoReady", "OnVideoError", "OnVideoPlay", "OnVideoPause", "OnVideoLoop",
            "InputJump", "InputUse", "InputGrab", "InputDrop", "InputMoveHorizontal", "InputMoveVertical", "InputLookHorizontal", "InputLookVertical",
            "OnPlayerRespawn", "OnSpawn", "OnStringLoadSuccess", "OnStringLoadError", "OnImageLoadSuccess", "OnImageLoadError",
            "PostLateUpdate", "OnAvatarChanged", "OnAvatarEyeHeightChanged", "OnPlayerRestored", "OnPlayerDataUpdated",
        };
    }
}
