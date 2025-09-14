// Assets/Scripts/Tests/EditMode/PoolTests.cs
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Systems.Pooling;
using System.Collections;

namespace Tests.EditMode
{
    /// <summary>
    /// EditMode tests for the Pool<T> implementation.
    /// These tests create a temporary prefab GameObject with a small TestComponent,
    /// create a Pool<TestComponent> from it, exercise Get/Return/Prewarm/Clear paths,
    /// and then clean up objects created during the test.
    /// </summary>
    public class PoolTests
    {
        private class TestComponent : MonoBehaviour
        {
            // Optional: implement IPoolable to verify lifecycle hooks do not crash.
            public bool WasPreparedForGet = false;
            public bool WasReturnedToPool = false;

            public void ResetForPoolCreation() { /* no-op for test */ }

            public void PrepareForGet() { WasPreparedForGet = true; }

            public void OnReturnedToPool() { WasReturnedToPool = true; }
        }

        private GameObject _prefab;
        private Pool<TestComponent> _pool;

        [SetUp]
        public void SetUp()
        {
            // Create a prefab-like GameObject for the pool to instantiate from.
            _prefab = new GameObject("PoolTest_Prefab");
            _prefab.AddComponent<TestComponent>();

            // Ensure the prefab is inactive by default (simulating a design-time prefab)
            _prefab.SetActive(false);

            // Create the pool with a small initial size.
            _pool = new Pool<TestComponent>(_prefab, parent: null, initialSize: 3, autoExpand: true, maxSize: 0);
        }

        [TearDown]
        public void TearDown()
        {
            // Clear pool which should destroy pooled instances and parent container.
            if (_pool != null)
            {
                _pool.Clear();
                _pool = null;
            }

            // Destroy the prefab used for instantiation
            if (_prefab != null)
            {
#if UNITY_EDITOR
                Object.DestroyImmediate(_prefab);
#else
                Object.Destroy(_prefab);
#endif
                _prefab = null;
            }

            // Also cleanup any "Pools" root left behind
            var root = GameObject.Find("Pools");
            if (root != null)
            {
#if UNITY_EDITOR
                Object.DestroyImmediate(root);
#else
                Object.Destroy(root);
#endif
            }
        }

        [Test]
        public void Prewarm_CreatesInactiveInstances()
        {
            // After construction with initialSize=3, the pool should have at least 3 inactive instances
            Assert.GreaterOrEqual(_pool.InactiveCount, 3, "Pool did not prewarm expected number of inactive instances.");
            Assert.AreEqual(0, _pool.ActiveCount, "No instances should be active immediately after prewarm.");
        }

        [Test]
        public void Get_ReturnsInstance_And_ActiveCountIncrements()
        {
            var beforeInactive = _pool.InactiveCount;
            var instance = _pool.Get();

            Assert.IsNotNull(instance, "Get() returned null instance.");
            Assert.IsTrue(instance.gameObject.activeSelf, "Instance should be active after Get().");
            Assert.AreEqual(1, _pool.ActiveCount, "ActiveCount should be 1 after one Get().");
            Assert.Less(_pool.InactiveCount, beforeInactive, "InactiveCount should decrease after Get().");

            // The instance should be of TestComponent type and have PrepareForGet invoked if implemented
            var tc = instance as TestComponent;
            // Note: our Pool only calls PrepareForGet() if the component implements IPoolable.
            // The TestComponent above doesn't implement IPoolable; so we won't assert WasPreparedForGet here.
        }

        [Test]
        public void Return_PutsInstanceBack_And_InactiveCountIncrements()
        {
            var instance = _pool.Get();
            Assert.AreEqual(1, _pool.ActiveCount);

            _pool.Return(instance);

            Assert.AreEqual(0, _pool.ActiveCount, "ActiveCount should be 0 after returning the only active instance.");
            Assert.GreaterOrEqual(_pool.InactiveCount, 1, "InactiveCount should be at least 1 after return.");
            Assert.IsFalse(instance.gameObject.activeSelf, "Returned instance should be inactive.");
        }

        [Test]
        public void Get_MultipleInstances_Then_Clear_DestroysAll()
        {
            // Get multiple instances
            var a = _pool.Get();
            var b = _pool.Get();
            var c = _pool.Get();

            Assert.AreEqual(3, _pool.ActiveCount, "Three instances should be active after 3 Gets.");

            // Return one to increase inactive
            _pool.Return(b);
            Assert.AreEqual(2, _pool.ActiveCount);
            Assert.GreaterOrEqual(_pool.InactiveCount, 1);

            // Clear pool - should remove inactive and active instances and destroy pool parent
            _pool.Clear();

            Assert.AreEqual(0, _pool.InactiveCount, "InactiveCount should be 0 after Clear().");
            Assert.AreEqual(0, _pool.ActiveCount, "ActiveCount should be 0 after Clear().");

            // The "Pools" root should be gone (clean up)
            var root = GameObject.Find("Pools");
            Assert.IsNull(root, "Pools root should be destroyed after Clear().");
        }

        [Test]
        public void Pool_AutoExpand_CreatesNewInstancesWhenEmpty()
        {
            // Create a small pool that is allowed to auto expand
            var smallPool = new Pool<TestComponent>(_prefab, parent: null, initialSize: 1, autoExpand: true, maxSize: 0);

            try
            {
                // Get more instances than initialSize to force expansion
                var i1 = smallPool.Get();
                var i2 = smallPool.Get(); // should create a new instance
                Assert.AreEqual(2, smallPool.ActiveCount, "Pool should have expanded and returned 2 active instances.");
            }
            finally
            {
                smallPool.Clear();
                // destroy small pool's created root if any
                var root = GameObject.Find("Pools");
                if (root != null)
                {
#if UNITY_EDITOR
                    Object.DestroyImmediate(root);
#else
                    Object.Destroy(root);
#endif
                }
            }
        }
    }
}