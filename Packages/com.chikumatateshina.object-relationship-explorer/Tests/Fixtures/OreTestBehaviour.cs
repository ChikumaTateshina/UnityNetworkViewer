using UnityEngine;
using UnityEngine.Events;

namespace ObjectRelationshipExplorer.Tests
{
    /// <summary>統合テスト用の参照を持つ MonoBehaviour。</summary>
    public sealed class OreTestBehaviour : MonoBehaviour
    {
        public GameObject target;
        public GameObject[] targets;
        public Component componentRef;
        public UnityEvent onUse = new UnityEvent();

        public void Use() { }
    }
}
