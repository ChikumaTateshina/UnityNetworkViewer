using UnityEngine;
using UnityEngine.Events;

namespace UnityNetworkViewer.Tests
{
    /// <summary>統合テスト用の参照を持つ MonoBehaviour。</summary>
    public sealed class UnvTestBehaviour : MonoBehaviour
    {
        public GameObject target;
        public GameObject[] targets;
        public Component componentRef;
        public UnityEvent onUse = new UnityEvent();

        public void Use() { }
    }
}
