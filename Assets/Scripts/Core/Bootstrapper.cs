// Assets/Scripts/Core/Bootstrapper.cs
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Systems.Analytics;
using Entities.Enemy;
using Systems.Pooling;


namespace Core
{
    /// <summary>
    /// Bootstrapper - lightweight, defensive system bootstrapper for the game.
    ///
    /// Responsibilities:
    ///  - Clear previous Services (useful for editor hot-reload / tests).
    ///  - Ensure a TimeService exists (creates one if needed).
    ///  - Create a "Systems" parent GameObject and attempt to initialize common systems
    ///    (PoolingSystem, AudioSystem, PersistenceManager, GameManager, DevTools) *if those types exist*.
    ///    This avoids compile-time dependencies on system implementations while still wiring them
    ///    up automatically when available.
    ///  - For any created MonoBehaviour system, it will attempt to register all implemented interfaces
    ///    with Services (first interface wins for registration). For plain non-Mono types (found by reflection),
    ///    it will instantiate an instance and register it as well.
    ///
    /// Design notes:
    ///  - This Bootstrapper uses reflection to remain resilient to phased development (you can add systems later,
    ///    and they'll be auto-registered when present).
    ///  - It intentionally avoids hard compile-time dependencies on other system classes so you can implement
    ///    them one-by-one.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class Bootstrapper : MonoBehaviour
    {
        [Header("Bootstrap Settings")]
        [Tooltip("If true, the bootstrapper clears previously registered Services on Awake. Useful during editor iteration.")]
        public bool clearServicesOnAwake = true;

        [Tooltip("If true, automatically call GameManager.StartRun() at the end of bootstrap if a GameManager is available.")]
        public bool startRunOnBoot = false;

        [Tooltip("Optional deterministic seed passed to systems that support seeded initialization (Wave/Spawner etc).")]
        public int seed = 0;

        // Parent object to hold created system MonoBehaviours
        private GameObject _systemsRoot;

        private readonly string[] _preferredSystemTypeNames = new[]
        {
            "PoolingSystem",
            "AudioSystem",
            "PersistenceManager",
            "GameManager",
            "DevOverlay",
            "SpawnConsole",
            "EnemySpawner"  
        };

        private void Awake()
        {
            if (clearServicesOnAwake)
            {
                Services.Clear();
                Debug.Log("[Bootstrapper] Cleared Services registry.");
            }

            EnsureSystemsRoot();
            EnsureTimeService();
            InitializeKnownSystems();
            RegisterEnemyTypes(); 
            RegisterStandaloneAnalyticsFallback();

            if (startRunOnBoot)
            {
                TryStartGameManager();
            }
        }

        private void EnsureSystemsRoot()
        {
            _systemsRoot = GameObject.Find("Systems");
            if (_systemsRoot == null)
            {
                _systemsRoot = new GameObject("Systems");
                DontDestroyOnLoad(_systemsRoot);
                Debug.Log("[Bootstrapper] Created Systems root GameObject.");
            }
            else
            {
                DontDestroyOnLoad(_systemsRoot);
            }
        }

        /// <summary>
        /// Ensure a TimeService is present in the scene. If not, create a GameObject and attach TimeService.
        /// TimeService registers itself with Services during its Awake/OnEnable, but we also ensure that
        /// the Services registry eventually has an ITimeService entry.
        /// </summary>
        private void EnsureTimeService()
        {
            // If already registered, don't create another.
            if (Services.TryGet<ITimeService>(out var _existing))
            {
                Debug.Log("[Bootstrapper] ITimeService already registered.");
                return;
            }

            // Try to find existing TimeService component in scene
            var existingComponent = FindObjectOfType(typeof(TimeService)) as TimeService;
            if (existingComponent != null)
            {
                // TimeService MonoBehaviour will register itself in Awake/OnEnable
                Debug.Log("[Bootstrapper] Found existing TimeService component in scene.");
                return;
            }

            // Create a new TimeService GameObject and attach component
            var go = new GameObject("TimeService");
            go.transform.SetParent(_systemsRoot.transform);
            var ts = go.AddComponent<TimeService>();
            // TimeService will register itself in Awake/OnEnable. Ensure it persists across scenes.
            DontDestroyOnLoad(go);
            Debug.Log("[Bootstrapper] Created TimeService GameObject and component.");
        }

        /// <summary>
        /// Attempt to dynamically find and instantiate known system types (by simple name).
        /// If the type implements MonoBehaviour, it will be added as a component to the Systems root.
        /// If it is a plain class, an instance will be constructed via Activator.CreateInstance and then
        /// registered in Services for any interfaces it implements.
        /// </summary>
        private void InitializeKnownSystems()
        {
            foreach (var shortName in _preferredSystemTypeNames)
            {
                var type = FindTypeBySimpleName(shortName);
                if (type == null)
                {
                    Debug.Log($"[Bootstrapper] Type '{shortName}' not found in loaded assemblies. (skipping)");
                    continue;
                }

                try
                {
                    if (typeof(MonoBehaviour).IsAssignableFrom(type))
                    {
                        // Add as component to Systems root if not already present
                        var component = _systemsRoot.GetComponent(type) ?? _systemsRoot.AddComponent(type);
                        DontDestroyOnLoad(((MonoBehaviour)component).gameObject);

                        // Attempt to register implemented interfaces
                        RegisterInterfacesFromInstance(component);
                        Debug.Log($"[Bootstrapper] Added MonoBehaviour system '{type.FullName}' and registered its interfaces.");
                    }
                    else
                    {
                        // Plain class: instantiate and register its interfaces
                        var instance = Activator.CreateInstance(type);
                        RegisterInterfacesFromInstance(instance);
                        Debug.Log($"[Bootstrapper] Instantiated non-Mono system '{type.FullName}' and registered its interfaces.");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Bootstrapper] Failed to initialize system '{shortName}': {e.Message}");
                }
            }
        }

        /// <summary>
        /// If no analytics provider is registered, register a simple null analytics so callers can safely log.
        /// </summary>
        private void RegisterStandaloneAnalyticsFallback()
        {
            if (!Services.TryGet<IAnalyticsProvider>(out var _))
            {
                // Try to find a concrete NullAnalytics type; otherwise, register a tiny runtime fallback.
                var nullAnalyticsType = FindTypeBySimpleName("NullAnalytics");
                if (nullAnalyticsType != null && !typeof(MonoBehaviour).IsAssignableFrom(nullAnalyticsType))
                {
                    try
                    {
                        var instance = Activator.CreateInstance(nullAnalyticsType) as IAnalyticsProvider;
                        if (instance != null)
                        {
                            Services.Register<IAnalyticsProvider>(instance, replaceExisting: true);
                            Debug.Log("[Bootstrapper] Registered NullAnalytics instance via reflection.");
                            return;
                        }
                    }
                    catch { /* swallow and fallback below */ }
                }

                // Minimal runtime null analytics
                var fallback = new NullAnalytics();
                Services.Register<IAnalyticsProvider>(fallback, replaceExisting: true);
                Debug.Log("[Bootstrapper] Registered fallback NullAnalytics implementation.");
            }
            else
            {
                Debug.Log("[Bootstrapper] Analytics provider already registered.");
            }
        }

        private void TryStartGameManager()
        {
            if (Services.TryGet<IGameManager>(out var gm))
            {
                try
                {
                    gm.StartRun();
                    Debug.Log("[Bootstrapper] Started GameManager run.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Bootstrapper] GameManager.StartRun threw: {e.Message}");
                }
            }
            else
            {
                // Try to find a GameManager MonoBehaviour on the Systems root via reflection — if found, call StartRun
                var gmType = FindTypeBySimpleName("GameManager");
                if (gmType != null)
                {
                    var gmComp = _systemsRoot.GetComponent(gmType) as MonoBehaviour;
                    if (gmComp != null)
                    {
                        // Try to invoke StartRun via reflection in a safe manner
                        var mi = gmType.GetMethod("StartRun", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi != null)
                        {
                            mi.Invoke(gmComp, null);
                            Debug.Log("[Bootstrapper] Invoked GameManager.StartRun via reflection.");
                            return;
                        }
                    }
                }

                Debug.Log("[Bootstrapper] No GameManager found to start.");
            }
        }

        /// <summary>
        /// Inspect the instance's implemented interfaces and register the instance under each interface type
        /// in Services (first interface encountered will be used to register if you want to limit, but here we register all).
        /// </summary>
        private void RegisterInterfacesFromInstance(object instance)
        {
            if (instance == null) return;

            var instanceType = instance.GetType();
            var interfaces = instanceType.GetInterfaces();

            // Register under each interface (replaceExisting = true to favor our bootstrapped instance)
            foreach (var iface in interfaces)
            {
                try
                {
                    // Only register interfaces that are likely to be service contracts (heuristic: interface name starts with 'I')
                    if (!iface.IsInterface) continue;
                    if (!iface.Name.StartsWith("I")) continue;

                    // Use Services.Register<T>(instance) via reflection:
                    var registerGeneric = typeof(Services).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Register" && m.IsGenericMethod && m.GetParameters().Length >= 1);

                    if (registerGeneric != null)
                    {
                        var generic = registerGeneric.MakeGenericMethod(iface);
                        // second parameter is replaceExisting = true (we pass true)
                        generic.Invoke(null, new object[] { instance, true });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Bootstrapper] Failed to register interface {iface.FullName} for instance {instanceType.FullName}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Utility: search loaded assemblies for a type whose simple name equals <paramref name="simpleName"/>.
        /// Returns the first match or null.
        /// </summary>
        private Type FindTypeBySimpleName(string simpleName)
        {
            // Search loaded assemblies - exclude dynamic/third-party rarely used ones if desired
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type t = null;
                // Try quick path: GetTypes can throw for some assemblies in restricted platforms; handle gracefully.
                try
                {
                    t = asm.GetTypes().FirstOrDefault(x => x.Name == simpleName);
                }
                catch
                {
                    continue;
                }

                if (t != null) return t;
            }

            return null;
        }


        private void RegisterEnemyTypes()
        {
            if (!Services.TryGet<EnemySpawner>(out var spawner))
            {
                Debug.LogWarning("[Bootstrapper] No EnemySpawner found to register enemy types.");
                return;
            }

            // Try to get the pooling system
            Services.TryGet<IPoolingSystem>(out var pooling);

            // Load enemy data assets
            var enemyDataList = new[]
            {
                Resources.Load<EnemyData>("Enemies/EnemyData_Basic"),
                Resources.Load<EnemyData>("Enemies/EnemyData_Fast"),
                Resources.Load<EnemyData>("Enemies/EnemyData_Shield")
            };

            foreach (var data in enemyDataList)
            {
                if (data == null)
                {
                    Debug.LogError("Failed to load an EnemyData asset from Resources/Enemies/");
                    continue;
                }

                // Register enemy type with the spawner
                spawner.RegisterEnemyType(data);

                // Prewarm the pool for this enemy if pooling system exists
                if (pooling != null && data.prefab != null) // <-- lowercase prefab
                {
                    pooling.Prewarm<EnemyBase>(data.prefab, 5); 
                }
            }

            Debug.Log("[Bootstrapper] Registered enemy types and prewarmed pools.");
        }
    }
}