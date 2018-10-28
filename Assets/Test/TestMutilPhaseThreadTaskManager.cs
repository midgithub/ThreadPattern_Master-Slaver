using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using FM.Threading;
using RenderSection = FM.Threading.TestRenderSection;

namespace FM.Threading {
    public class TestRenderSection {
        public volatile Task currentTask;
        public long lastRebuildTicks;//用于标记当前的更新是否是最新的更新
        public int DistanceToPlayer { get { return Mathf.FloorToInt(Vector3.Distance(Vector3.zero, position) * 2); } }
        public Transform player;
        public Vector3 position;
    }

    public class RenderTask : Task {
        protected override int MaxPhaseNum() { return 2; }
        protected override int CompareFunc(Task _other) {
            if (_other.currentParseIdx != this.currentParseIdx)
                return _other.currentParseIdx - this.currentParseIdx;
            var _osection = _other.param as TestRenderSection;
            var _tsection = param as TestRenderSection;
            return _tsection.DistanceToPlayer - _osection.DistanceToPlayer;
        }
    }

    public class TestMasterSlaveThreadManager : MasterSlaveThreadManager {
        public TestMasterSlaveThreadManager(int _threadCount, int _maxTaskCount, System.Func<MasterSlaveThreadManager, int, ThreadWorker> _WorkCreateFunc, bool _alsoRunInMainThread = false) :
            base(_threadCount, _maxTaskCount, _WorkCreateFunc, _alsoRunInMainThread) { }

        //每帧的初始化时间
        protected float frameDeadlineS;
        protected override void SetTimer(long _maxRunTimeMs) { frameDeadlineS = Time.realtimeSinceStartup + _maxRunTimeMs * 0.001f; }
        //是否超时
        protected override bool IsTimeOut() { return Time.realtimeSinceStartup > frameDeadlineS; }

        private Transform parent;
        //处理第一阶段任务 可能在任何线程
        protected override void OnDealTaskPreparePart(Task _task) {
            WasteCPUTime(10);
        }

        //处理最后一阶段任务 在主线程
        protected override void OnDealTaskCommitPart(Task _task) {
            WasteCPUTime(1);
            if (parent == null) {
                parent = new GameObject("GoParent").transform;
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.parent = parent;
            go.transform.position = (_task.param as TestRenderSection).position;
            go.transform.localScale = new Vector3(1, 1, 1);
#if TEST_MAIN_THRED_BUG
            if (_task.is_deal_in_main_thread)
            {
                go.transform.localScale = new Vector3(1, 4, 1);
            }
#endif
        }
        public void CommitRebuildSectionTask(TestRenderSection _section) {
            Debug.Assert(IsInMainThread);
            if (_section.currentTask != null) {
                _section.currentTask.IsInterrupted = true;
                _section.currentTask = null;
            }
            var _task = CreateCollideTask(_section);
            _section.currentTask = _task;
            _section.lastRebuildTicks = _task.createTicks;
            _task.OnFinishedEvent += (_tk) => {
                if (_tk.Exception != null) {
                    Debug.LogException(_tk.Exception);
                    return;
                }
                var _sec = _tk.param as TestRenderSection;
                if (_tk.createTicks < _sec.lastRebuildTicks) {
                    Debug.LogError("Not the newest task");
                    return;
                }
                _sec.currentTask = null;
                if (!_tk.IsFinished) {
                    Debug.LogError("Logic Error:");
                }
                System.Threading.Thread.Sleep(3);
            };

            _task.OnInterruptEvent += (_tk) => {
                Debug.LogError("OnInterrupt deal num= " + _tk.dealNum + " IsFinied = " + _tk.IsFinished);
                System.Threading.Thread.Sleep(2);
            };
            AnyThreadTasks.Enqueue(_task);
        }

        private Task CreateCollideTask(TestRenderSection _section) {
            var _task = new RenderTask();
            var _tick = GetCurrentTicks();
            _task.createTicks = _tick;
            _task.param = _section;
            return _task;
        }

        void WasteCPUTime(int _ms) {
            var d1 = System.DateTime.Now;
            var _run_ms = Mathf.Lerp(0.5f, 2.5f, (float)(new System.Random().NextDouble())) * _ms;
            while (true) {
                var ms = System.DateTime.Now.Subtract(d1).TotalMilliseconds;
                if (ms > _run_ms) {
                    break;
                }
                System.Threading.Thread.Sleep(1);
            }
        }
    }
}
public class TestMutilPhaseThreadTaskManager : UnityEngine.MonoBehaviour {
    TestMasterSlaveThreadManager renderTaskMgr;
    public float deltaTime = 0;
    public bool isAlsoRunInMainThread;
    public int maxUpdateTimeMs = 20;
    Coroutine currentCour;
    public void Start() {
        Debug.LogError("Start");
        renderTaskMgr = new TestMasterSlaveThreadManager(4, 10, (_master, _idx) => { return new ThreadWorker(_master); }, isAlsoRunInMainThread);
        renderTaskMgr.Start();
        currentCour = StartCoroutine(CreateCubePos());
    }
    public int processingTaskCount = 0;
    public int secondPassTaskCount = 0;
    public int finishedTaskCount = 0;
    public int interruptedTaskCount = 0;

    public int lastUpdateTime = 0;
    public void Update() {
        deltaTime = Time.deltaTime;
        var _t = Time.realtimeSinceStartup;
        if (!isDestroyed) {
            renderTaskMgr.Update(maxUpdateTimeMs);
        }
        lastUpdateTime = (int)((Time.realtimeSinceStartup - _t) * 1000);
        processingTaskCount = renderTaskMgr.GetAnyThreadTaskCount();
        secondPassTaskCount = renderTaskMgr.GetSecondPassTaskCount();
        finishedTaskCount = renderTaskMgr.GetMainThreadTaskCount();
        interruptedTaskCount = renderTaskMgr.GetCurFrameTaskCount();
    }
    bool isDestroyed = false;
    public void OnDestroy() {
        if (isDestroyed) return;
        isDestroyed = true;
        StopCoroutine(currentCour);
        renderTaskMgr.OnDestroy();
    }
    IEnumerator CreateCubePos() {
        int x = 0, z = 0;
        while (true) {
            if (renderTaskMgr.CanCommitTask()) {
                CommitTask(new Vector3(x, 0, z));
                x++;
                if (x > 100) {
                    x = 0;
                    z++;
                }
            } else {
                yield return null;
            }
        }
    }
    List<RenderSection> sections = new List<RenderSection>();
    void CommitTask(Vector3 _pos) {
        var _sec = new RenderSection() { player = transform, position = _pos };
        sections.Add(_sec);
        renderTaskMgr.CommitRebuildSectionTask(_sec);
    }

    void OnGUI() {
        int breaknum = 5;

        if (UnityEngine.GUILayout.Button("Interrupt one task")) {
            int i = 0;
            for (int _idx = sections.Count - 1; _idx > 0; --_idx) {
                var item = sections[_idx];
                var _task = item.currentTask;
                if (_task != null && Random.value > 0.8f) {
                    _task.IsInterrupted = true;
                    ++i;
                }
                if (i >= breaknum) { break; }
            }
            Debug.LogError("Break num  " + i);
        } 
        
        if (UnityEngine.GUILayout.Button("Stop")) {
            OnDestroy();
        }
    }
}