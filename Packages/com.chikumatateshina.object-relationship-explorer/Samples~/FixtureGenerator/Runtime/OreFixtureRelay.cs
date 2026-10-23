using UnityEngine;
using UnityEngine.Events;

namespace ObjectRelationshipExplorer.Samples
{
    /// <summary>Fixture 用の参照コンポーネント（Fixture 専用。Fixture Scene はビルド対象にしないこと）。</summary>
    public sealed class OreFixtureRelay : MonoBehaviour
    {
        public GameObject target;
        public GameObject[] targets;
        public Animator animator;
        public GameObject missing;
        public UnityEvent onPress = new UnityEvent();
    }
}
