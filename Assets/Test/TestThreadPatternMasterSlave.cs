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
        public RenderTask(System.Object param) : base(param) { }
        protected override int MaxPhaseNum { get { return 2; } }
        protected override int CompareFunc(Task _other) {
            if (_other.currentParseIdx != this.currentParseIdx)
                return _other.currentParseIdx - this.currentParseIdx;
            var _osection = _other.Param as TestRenderSection;
            var _tsection = Param as TestRenderSection;
            return _tsection.DistanceToPlayer - _osection.DistanceToPlayer;
        }
        public override EThreadContext GetTargetQueueType() {
            if (IsMustRunInCurFrame) {
                return EThreadContext.MainThreadAndInCurFrame;
            } else {
                var _curTaskPhase = (ETestTaskPhase)currentParseIdx;
                switch (_curTaskPhase) {
                    case ETestTaskPhase.FirstAny:
                        return EThreadContext.AnyThread;
                    case ETestTaskPhase.SecondMain:
                        return EThreadContext.MainThread;
                    case ETestTaskPhase.Finished:
                        return EThreadContext.MainThread;
                    default:
                        Debug.LogError("Should not call again");
                        return EThreadContext.MainThread;
                }
            }
        }
    }

}
public enum ETestTaskPhase {
    FirstAny,
    SecondMain,
    Finished,
}
public class TestThreadPatternMasterSlave : UnityEngine.MonoBehaviour {
    ThradPartternMasterSlaver taskMgr = new ThradPartternMasterSlaver();
    public bool isAlsoRunInMainThread;
    public int maxUpdateTimeMs = 20;
    public int AnyThreadCount = 0;
    public int MainThreadTaskCount = 0;
    public int CurFrameTaskCount = 0;
    public float DeltaTimeMs = 0;
    bool isDestroyed = false;
    private Transform parentTrans;
    List<RenderSection> sections = new List<RenderSection>();
    Coroutine currentCour;


    public void Start() {
        Debug.LogError("Start");
        taskMgr.DoStart(4, 10, CreateWorker, DealTask);
        currentCour = StartCoroutine(CreateCubePos());
    }
    ThreadWorker CreateWorker(ThradPartternMasterSlaver _mgr, int _idx) {
        return new ThreadWorker(taskMgr);
    }
    public int CurFrameMaxRunMs;
    public void Update() {
        DeltaTimeMs = (int)(Time.deltaTime * 1000);
        var _t = Time.realtimeSinceStartup;
        if (!isDestroyed) {
            taskMgr.DoUpdate((int)(DeltaTimeMs));
        }
        AnyThreadCount = taskMgr.GetAnyThreadTaskCount();
        MainThreadTaskCount = taskMgr.GetMainThreadTaskCount();
        CurFrameTaskCount = taskMgr.GetCurFrameTaskCount();
        CurFrameMaxRunMs = taskMgr.CurFrameMaxRunMs;
    }
    public void OnDestroy() {
        if (isDestroyed) return;
        isDestroyed = true;
        StopCoroutine(currentCour);
        taskMgr.DoDestroy();
    }
    IEnumerator CreateCubePos() {
        int x = 0, z = 0;
        while (true) {
            if (taskMgr.CanCommitTask()) {
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
    protected void DealTask(Task _task) {
        var _taskPhase = (ETestTaskPhase)_task.currentParseIdx;
        switch (_taskPhase) {
            case ETestTaskPhase.FirstAny:
                OnDealTaskPreparePart(_task);
                break;
            case ETestTaskPhase.SecondMain:
                OnDealTaskCommitPart(_task);
                break;
            default:
                Debug.LogError("Task state invalid!" + _task.ToString());
                break;
        }
    }


    //处理第一阶段任务 可能在任何线程
    protected void OnDealTaskPreparePart(Task _task) {
        //Debug.Log("OnDealTaskPreparePart is InMainThread = " + taskMgr.IsInMainThread);
        WasteCPUTime(10);
    }

    //处理最后一阶段任务 在主线程
    protected void OnDealTaskCommitPart(Task _task) {
        //Debug.Log("Doing OnDealTaskCommitPart is InMainThread = " + taskMgr.IsInMainThread);
        Debug.Assert(taskMgr.IsInMainThread, "Must in MainThread but is not" + _task.ToString());
        WasteCPUTime(1);
        if (parentTrans == null) {
            parentTrans = new GameObject("GoParent").transform;
        }
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.parent = parentTrans;
        go.transform.position = (_task.Param as TestRenderSection).position;
        go.transform.localScale = new Vector3(1, 1, 1);
#if TEST_MAIN_THRED_BUG
        if (_task.isDealInMainThread) {
            go.transform.localScale = new Vector3(1, 4, 1);
        }
#endif
    }

    void WasteCPUTime(int _ms) {
        var d1 = System.DateTime.Now;
        var _run_ms = Mathf.Lerp(0.5f, 2.5f, (float)(new System.Random().NextDouble())) * _ms;
        while (true) {
            var ms = System.DateTime.Now.Subtract(d1).TotalMilliseconds;
            if (ms > _run_ms) {
                break;
            }
            // do somthing 
            System.Threading.Thread.Sleep(1);
        }
    }


    void CommitTask(Vector3 _pos) {
        var _section = new RenderSection() { player = transform, position = _pos };
        sections.Add(_section);
        if (_section.currentTask != null) {
            _section.currentTask.IsInterrupted = true;
            _section.currentTask = null;
        }

        var _task = new RenderTask(_section);
        _section.currentTask = _task;
        _section.lastRebuildTicks = _task.createTicks;
        _task.OnFinishedEvent += (_tk) => {
            //Debug.Log("FinishedCallBack is InMainThread = " + taskMgr.IsInMainThread);
            if (_tk.Exception != null) {
                Debug.LogException(_tk.Exception);
                return;
            }
            var _sec = _tk.Param as TestRenderSection;
            if (_tk.createTicks < _sec.lastRebuildTicks) {
                Debug.LogError("Not the newest task");
                return;
            }
            _sec.currentTask = null;
            if (!_tk.IsFinished) {
                Debug.LogError("Logic Error:");
            }
            System.Threading.Thread.Sleep(1);
        };

        _task.OnInterruptEvent += (_tk) => {
            Debug.LogError("OnInterrupt deal num= " + _tk.currentParseIdx + " IsFinied = " + _tk.IsFinished);
            System.Threading.Thread.Sleep(2);
        };

        taskMgr.AddTask(_task);
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