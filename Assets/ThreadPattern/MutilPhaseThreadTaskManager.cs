/// <summary>
/// author:jiepengtan 
/// 
/// 约定：
/// 所有Blocking命名开头的函数 都可能导致线程阻塞 不应该主线程使用
/// 所有NoBlocking命名开头的函数 一定不会导致线程阻塞 可以用于主线程使用
/// 
/// 使用方式：
/// Task：如果希望自己的Task 可以被多遍遍历 可以重载  MaxPhaseNum() { return 1; } //返回希望被处理的次数
/// MasterSlaveThreadManager：一般只要重载
///    protected virtual void OnDealTaskPreparePart(Task _task) { }
///    protected virtual void OnDealTaskCommitPart(Task _task) { }
///    函数即可
///    OnDealTaskPreparePart 可能在任何线程被调用
///    OnDealTaskCommitPart 只可能在主线程调用
///   
/// //建议 
/// 使用：Thread local storage 请 继承ThreadWorker后添加相应的成员 并重写ResetThreadLoacalStorage
/// 使用：Task local storage 请继承Task后添加相应的成员
/// 
/// 具体使用方式可以参考：TestMutilPhaseThreadTaskManager
/// </summary>

//#define TEST_MAIN_THRED_BUG // 测试主线程参与是否会导致bug
//#define DEBUG_THREAD_PHASE_ID //开启线程阶段调试
using UnityEngine;
using System.Collections.Generic;
using System.Threading;
using System;


namespace FM.Threading {
    /// <summary>
    /// 任务运行的线程环境限制
    /// </summary>
    public enum ETaskRuningContextType {
        /// <summary>
        /// 必须在主线程中运行，且必须做当前帧完成
        /// </summary>
        MustRunInMainThreadAndInCurFrame,
        /// <summary>
        /// 必须在主线程中运行，但不一定是本帧
        /// </summary>
        MustRunInMainThread,
        /// <summary>
        /// 可以运行在任意线程
        /// </summary>
        RunInAnyThread,
    }

    public class Task : System.IComparable<Task> {

        public ThreadWorker threadLocalStorage;

        public System.Object param;
        public long createTicks;
        public int dealNum;//经历了多少次流转 比current_parse_idx 更加的细粒度
        public int currentParseIdx { get; protected set; }// 
#if TEST_MAIN_THRED_BUG
        public bool isDealInMainThread;
#endif
#if DEBUG_THREAD_PHASE_ID
        public List<int> workedThreadIds = new System.Collections.Generic.List<int>();
#endif

        private bool isNormalFinished;//任务正常完成
        public bool IsFinished {
            get {
                if (IsInterrupted || Exception != null) {
                    return true;
                }
                return isNormalFinished;
            }
            set {
                isNormalFinished = value;
            }
        }
        public volatile bool IsInterrupted;
        public volatile System.Exception Exception;


        public System.Action<Task> OnFinishedEvent;
        public System.Action<Task> OnInterruptEvent;
        public bool IsMustRunInCurFrame = false;


        /// <summary>
        /// 当前阶段任务在那种类型的线程中执行
        /// </summary>
        /// <returns></returns>
        public virtual ETaskRuningContextType CurParseThradContextType() {
            if (IsMustRunInCurFrame) {
                return ETaskRuningContextType.MustRunInMainThreadAndInCurFrame;
            } else {
                return HasNextPhase() ? ETaskRuningContextType.RunInAnyThread : ETaskRuningContextType.MustRunInMainThread;
            }
        }
        /// <summary>
        /// 第一次希望被投递的队列类型
        /// </summary>
        /// <returns></returns>
        public virtual ETaskRuningContextType GetInitThreadContentType() {
            if (IsMustRunInCurFrame) {
                return ETaskRuningContextType.MustRunInMainThreadAndInCurFrame;
            } else {
                return ETaskRuningContextType.RunInAnyThread;
            }
        }
        /// <summary>
        /// 任务被中断 回调，视情况是否需要重新提交任务
        /// </summary>
        public virtual void OnInterrupted() {
            if (OnInterruptEvent != null) OnInterruptEvent(this);
        }
        /// <summary>
        /// 可能失败  可能异常  可能成功 三种情况 回调
        /// </summary>
        public virtual void OnFinished() {
            if (OnFinishedEvent != null) OnFinishedEvent(this);
        }

        public bool HasNextPhase() { return currentParseIdx < MaxPhaseNum(); }
        public bool MoveNextPhase() { return ++currentParseIdx < MaxPhaseNum(); }

        protected virtual int MaxPhaseNum() { return 1; }
        protected virtual int CompareFunc(Task _other) {
            return _other.currentParseIdx - currentParseIdx;
        }

        int System.IComparable<Task>.CompareTo(Task _other) {
            return CompareFunc(_other);
        }
    }

    public class ThreadWorker {
        public ThreadWorker(MasterSlaveThreadManager _master) {
            master = _master;
        }
        private MasterSlaveThreadManager master;
        /// <summary>
        /// reset thread local storage
        /// </summary>
        public virtual void ResetThreadLoacalStorage() { }
        public bool IsFinisedRun = false;
        public void Run() {
            try {
                master.DealQueue(master.AnyThreadTasks, this, null, false);
            } catch (System.Threading.ThreadInterruptedException) {
                //正常结束
                IsFinisedRun = true;
                Debug.Log("Interrupt Stop");
                return;
            }
            IsFinisedRun = true;
            Debug.Log("Auto Stop");
        }

    }

    public interface IThreadManager {

    }
    /// <summary>
    /// 多阶段提交的多线程任务管理器
    /// </summary>
    public class MutilPhaseThreadTaskManager {

        /// <summary>
        /// 防主线程在投递相应的
        /// </summary>
        public class PendingEnqueueTasks {
            public IBlockQueue<Task> targetQueue;
            public Task task;
            public PendingEnqueueTasks(IBlockQueue<Task> _targetQueue, Task _task) {
                targetQueue = _targetQueue;
                task = _task;
            }
        }
        /// <summary>
        /// 为了防止主线程在将阶段性完成的任务进行重新派发的时候卡死，将这些无法投递的任务临时存起来
        /// 仅在主线程中调用  不需要加锁
        /// </summary>
        private List<PendingEnqueueTasks> waitEnqueueTasks = new List<PendingEnqueueTasks>();

        public IBlockQueue<Task> CurFrameTasks; //必须在主线程中执行，且必须在当前帧中完成
        public IBlockQueue<Task> MainThreadTasks; //必须在主线程中执行
        public IBlockQueue<Task> AnyThreadTasks;//多线程队列 所有的这些任务可能在任何线程中执行

        protected ThreadWorker mainThreadWorker;//主线程自己的局部环境
        private const int MAX_ENQUEUE_WAIT_TIME_MS = 3000;//最大等待时间
        private Thread mainThread;//出线程
        protected virtual IBlockQueue<Task> CreateBlockQueue(int count) {
            return new BlockingQueue<Task>(new PriorityQueue<Task>(), count);
        }

        //同时最大任务数量
        protected int MaxTaskCount { get; private set; }
        public bool IsNeedToStop = false;
        public int GetCurFrameTaskCount() { return CurFrameTasks.Count; }
        public int GetMainThreadTaskCount() { return MainThreadTasks.Count; }
        public int GetAnyThreadTaskCount() { return AnyThreadTasks.Count; }


        /// <summary>
        /// 等待派发的任务
        /// </summary>
        List<Task> waitDispatchTasks = new List<Task>();
        HashSet<Task> waitDispatchTaskSet = new HashSet<Task>();

        protected List<ThreadWorker> threadWorkers;
        protected List<Thread> threads;
        /// <summary>
        /// 最大等待其他线程自动结束时间
        /// </summary>
        public int MaxWaitOtherThreadStopTimeMs = 12;

        protected System.Func<MutilPhaseThreadTaskManager, int, ThreadWorker> WorkCreateFunc;

        #region 每帧运行时间控制
        /// <summary>
        /// 每帧最多可以运行的毫秒数，用于控制帧率 (单位毫秒)
        /// </summary>
        public int MaxRunTimePreFrameInMs = 10;
        //获取当前的时间戳
        public float realtimeSinceStartupInMs { get { return (DateTime.Now.Ticks / 10000.0f); } }

        public int TargetFrameDeltaTime = 33;//目标每帧跑多少毫秒
        protected float curFrameDeadline;//当前帧截至时间
        protected float lastFrameExcendTimeInMs;//上一帧超时时间
        //是否超时
        protected bool IsTimeOut() { return realtimeSinceStartupInMs > curFrameDeadline; }

        #endregion

        /// <summary>
        /// 当前是否是运行在主线程中
        /// </summary>
        protected bool IsInMainThread { get { return System.Threading.Thread.CurrentThread == mainThread; } }

        bool isInited = false;
        //主线程中的更新
        public void OnUpdate(long _deltaTimeMs) {
            if (!isInited)
                return;
            var _timePenaltyRate = Mathf.Min(1.0f, 1.0f * TargetFrameDeltaTime / _deltaTimeMs);//计算上一帧超时后的罚时，为了帧率更加的平衡
            curFrameDeadline = realtimeSinceStartupInMs + Mathf.Max((int)(MaxRunTimePreFrameInMs * _timePenaltyRate - lastFrameExcendTimeInMs), 0);

            //处理必须在这帧完成的事件的回调
            DispatchWaitEnqueueTasks(AnyThreadTasks);//将等待队列中的可以在其他线程中运行的任务派发给其他的线程中
            DealQueue(CurFrameTasks, mainThreadWorker, null, true);//处理本帧必须完成的任务，无时间限制
            DealQueue(MainThreadTasks, mainThreadWorker, IsTimeOut, true);//处理必须在主线程中完成的任务，有时间限制

            lastFrameExcendTimeInMs = realtimeSinceStartupInMs - curFrameDeadline;
        }

        public void Start(
            int _threadCount
            , int _maxTaskCount
            , System.Func<MutilPhaseThreadTaskManager, int, ThreadWorker> _WorkCreateFunc
            , int _MaxRunTimePreFrameInMs = 10
            ) {
            if (isInited) {
                Debug.LogError("重复初始化：逻辑错误");
                return;
            }
            isInited = true;
            mainThread = Thread.CurrentThread;
            threadWorkers = new List<ThreadWorker>();
            threads = new List<Thread>();

            MaxRunTimePreFrameInMs = _MaxRunTimePreFrameInMs;
            MaxTaskCount = _maxTaskCount;
            AnyThreadTasks = CreateBlockQueue(MaxTaskCount * 1);
            MainThreadTasks = CreateBlockQueue(MaxTaskCount * 2);
            CurFrameTasks = CreateBlockQueue(MaxTaskCount * 10);
            WorkCreateFunc = _WorkCreateFunc;
            mainThreadWorker = _WorkCreateFunc(this, -1);
            for (int _i = 0; _i < _threadCount; ++_i) {
                //每个线程都分配避免互相之间锁 而挂起
                var _worker = WorkCreateFunc(this, _i);
                Thread _thread = new Thread(_worker.Run);
                _thread.IsBackground = true;
                threadWorkers.Add(_worker);
                threads.Add(_thread);
            }
            foreach (var _thread in threads) {
                _thread.Start();
            }
        }

        public void OnDestroy() {
            isInited = false;
            //知被阻塞的线程 需要停止
            IsNeedToStop = true;
            //唤醒正在等待的所有线程
            MainThreadTasks.Close();
            AnyThreadTasks.Close();
            CurFrameTasks.Close();

            for (int _i = 0; _i < threads.Count; ++_i) {
                try {
                    threads[_i].Interrupt();//以中断的形式通知线程结束
                } catch (System.Threading.ThreadInterruptedException) {
                    Debug.LogWarning("Interrupted whilst waiting for worker to die");
                }
            }
            //等待线程主动结束
            var _initTime = DateTime.Now;
            while (true) {
                bool _isFinishedAll = true;
                for (int _i = 0; _i < threadWorkers.Count; ++_i) {
                    if (!threadWorkers[_i].IsFinisedRun) {
                        _isFinishedAll = false;
                    }
                }
                if (_isFinishedAll) {
                    break;
                }
                Thread.Sleep(1);
                if ((_initTime - DateTime.Now).TotalMilliseconds > MaxWaitOtherThreadStopTimeMs) {
                    break;
                }
            }

            //强制结束
            for (int _i = 0; _i < threadWorkers.Count; ++_i) {
                if (!threadWorkers[_i].IsFinisedRun) {
                    threads[_i].Abort();
                }
            }
            threads.Clear();

            //清理未完成的所有任务
            Action<IBlockQueue<Task>> _FuncClearQueue = (_queue) => {
                while (!_queue.IsEmpty) {
                    Task _task = null;
                    _queue.TryDequeue(out _task);
                    if (_task != null && _task.OnInterruptEvent != null) {
                        _task.OnInterruptEvent(_task);
                    }
                }
            };
            _FuncClearQueue(CurFrameTasks);
            _FuncClearQueue(MainThreadTasks);
            _FuncClearQueue(AnyThreadTasks);

            Debug.Log("ThreadManager Finish Clear");
        }

        /// <summary>
        /// 中断任务
        /// </summary>
        /// <param name="_task"></param>
        public void InterruptTask(Task _task) {
            if (!IsInMainThread) {
                Debug.LogError("任务的添加：中断任务必须在主线程中进行");
                return;
            }
            _task.IsInterrupted = true;
        }
        /// <summary>
        /// 是否可以提交任务
        /// </summary>
        /// <returns></returns>
        public virtual bool CanCommitTask() {
            return waitDispatchTasks.Count < MaxTaskCount * 3;
        }

        //可能在任何线程回调
        protected void DealTask(Task _task) {
#if DEBUG_THREAD_PHASE_ID
            _task.workedThreadIds.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
#endif
            OnDealTask(_task);
        }

        protected virtual void OnDealTask(Task _task) {

        }        /// <summary>
        /// 添加的任务
        /// </summary>
        /// <param name="_task"></param>
        public void AddTask(Task _task) {
            if (!IsInMainThread) {
                Debug.LogError("任务的添加：必须在主线程");
                return;
            }
            if (waitDispatchTaskSet.Add(_task)) {
                waitDispatchTasks.Add(_task);
            } else {
                Debug.LogError("逻辑错误：添加重复的任务 " + _task.ToString());
            }
        }



        #region Help Func
        IBlockQueue<Task> GetQueueFromType(ETaskRuningContextType _queueType) {
            IBlockQueue<Task> _nextQueue = null;
            switch (_queueType) {
                case ETaskRuningContextType.MustRunInMainThreadAndInCurFrame:
                    _nextQueue = CurFrameTasks;
                    break;
                case ETaskRuningContextType.MustRunInMainThread:
                    _nextQueue = MainThreadTasks;
                    break;
                case ETaskRuningContextType.RunInAnyThread:
                    _nextQueue = AnyThreadTasks;
                    break;
                default:
                    break;
            }
            return _nextQueue;
        }
        void DispatchWaitDispatchTasks() {
            var _count = waitDispatchTasks.Count;
            for (int i = 0; i < _count; i++) {
                var _info = waitDispatchTasks[i];
                var _targetQueue = GetQueueFromType(_info.GetInitThreadContentType());
                if (_targetQueue.TryEnqueue(_info)) {
                    waitEnqueueTasks.RemoveAt(i);
                    --i;
                    --_count;
                }
            }
        }

        void DispatchWaitEnqueueTasks(IBlockQueue<Task> _targetQueue) {
            var _count = waitEnqueueTasks.Count;
            for (int i = 0; i < _count; i++) {
                var _info = waitEnqueueTasks[i];
                if (_info.targetQueue == _targetQueue) {
                    //注意必须在主线程执行的队列  一定可以装进去
                    if (_targetQueue.TryEnqueue(_info.task)) {
                        waitEnqueueTasks.RemoveAt(i);
                        --i;
                        --_count;
                    } else {
                        break;
                    }
                }
            }
        }

        public void DealQueue(IBlockQueue<Task> _todoQueue, ThreadWorker _thread_worker, Func<bool> _FuncIsTimeOut, bool _isInMainThread = true) {
            while (IsNeedToStop) {
                Task _task = null;
                if (_isInMainThread) {
                    _task = _todoQueue.Dequeue();
                } else {
                    if (!_todoQueue.TryDequeue(out _task)) {
                        if (!TryDequeueWaitEqueueTasks(_todoQueue, out _task)) {
                            return;
                        }
                    }
                }
                _thread_worker.ResetThreadLoacalStorage();
                _task.threadLocalStorage = _thread_worker;
                if (!_task.IsFinished) {
                    //处理还未完成的任务
                    try {
                        DealTask(_task);
                        if (_task.MoveNextPhase()) {
                            //根据当前任务的状态派发到不同的队列中
                            var _queueType = _task.CurParseThradContextType();
                            var _nextQueue = GetQueueFromType(_queueType);
                            if (!_isInMainThread) {
                                //为了防止阻塞
                                if (!_nextQueue.TryEnqueue(_task)) {
                                    AddWaitEnqueueTask(_nextQueue, _task);
                                }
                            } else {
                                _nextQueue.Enqueue(_task);
                            }
                        }
                    } catch (System.Threading.ThreadInterruptedException) {
                        //正常现象
                        _task.IsInterrupted = true;
                        if (_isInMainThread) {
                            NoBlockingEnqueue(_task);
                        } else {
                            BlockingEnqueue(MainThreadTasks, _task);
                        }
                    } catch (System.Exception _e) {
                        _task.Exception = _e;
                        if (_isInMainThread) {
                            NoBlockingEnqueue(_task);
                        } else {
                            BlockingEnqueue(MainThreadTasks, _task);
                        }
                    }
                    if (_FuncIsTimeOut != null && _FuncIsTimeOut()) {
                        break;
                    }
                } else {
                    Debug.Assert(_isInMainThread, "LogicError: Callback Must in mainThread");
                    //处理已经完成了的任务，进行回调
                    try {
                        if (_task.IsInterrupted) {
                            _task.OnInterrupted();
                        } else {
                            _task.OnFinished();
                        }
                        if (_FuncIsTimeOut != null && _FuncIsTimeOut()) {
                            break;
                        }
                    } catch (Exception _e) {
                        //这里已经是处理完成的任务相关
                        Debug.LogError("处理任务完成回调抛出异常: " + _e + " _task " + _task.ToString());
                    }
                }
            }
        }

        public void AddWaitEnqueueTask(IBlockQueue<Task> _queue, Task _task) {
            waitEnqueueTasks.Add(new PendingEnqueueTasks(_queue, _task));
        }
        bool TryDequeueWaitEqueueTasks(IBlockQueue<Task> _targetQueue, out Task _task) {
            var _count = waitEnqueueTasks.Count;
            _task = null;
            for (int i = 0; i < _count; i++) {
                var _info = waitEnqueueTasks[i];
                if (_info.targetQueue == _targetQueue) {
                    _task = _info.task;
                    waitEnqueueTasks.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }


        public void NoBlockingEnqueue(Task _task) {
            if (_task.IsMustRunInCurFrame) {
                NoBlockingEnqueue(CurFrameTasks, _task);
            } else {
                NoBlockingEnqueue(MainThreadTasks, _task);
            }
        }

        public void NoBlockingEnqueue(IBlockQueue<Task> _queue, Task _task) {
            if (!_queue.TryEnqueue(_task)) {
                AddWaitEnqueueTask(_queue, _task);
            };
        }
        public void BlockingEnqueue(IBlockQueue<Task> _queue, Task _task) {
            _queue.Enqueue(_task);
        }

        #endregion

    }

}