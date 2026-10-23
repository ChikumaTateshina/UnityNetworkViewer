using UnityEngine;
using UnityEngine.Events;

namespace UnityNetworkViewer.Samples
{
    /// <summary>Fixture 用の参照コンポーネント（Fixture 専用。Fixture Scene はビルド対象にしないこと）。</summary>
    public sealed class UnvFixtureRelay : MonoBehaviour
    {
        public GameObject target;
        public GameObject[] targets;
        public Animator animator;
        public GameObject missing;
        public UnityEvent onPress = new UnityEvent();
    }
}
