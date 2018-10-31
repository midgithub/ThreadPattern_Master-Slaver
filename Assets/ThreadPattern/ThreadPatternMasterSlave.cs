/// <summary>
/// Create  by JiepengTan@gmail.com
/// 2018-10-30
/// 多线程调用框架：
/// 功能：
/// 1.支持每帧指定最大运行时间
/// 2.支持3中线程运行环境：主线程且必须在当前帧完成，必须在主线程完成，可以在任意线程中执行
/// 3.支持可指定同一个任务可在多阶段的在指定线程环境中执行，如第一阶段可以在任意线程中执行，最后阶段必须在主线程中执行
/// 4.支持时间分摊，如上一帧执行时间太长，这以帧的执行时间将会缩减，避免卡的情况
/// 5.支持任务中断
/// 
/// 任务完成的时候可以配置必须在主线程中回调。(也可以在任意线程回调，但不建议这样做)
/// 
/// 使用方式：
/// Task：
/// 如果希望自己的Task 可以被多遍遍历 可以重载 
///     MaxPhaseNum()//返回希望被处理的次数
///     GetCurParseThradContextType() //返回该任务第多少阶段需要在什么类型的线程中执行
///         MainThreadAndInCurFrame,//必须在主线程中运行，且必须做当前帧完成
///         MainThread,//必须在主线程中运行，但不一定是本帧
///         AnyThread,//可以运行在任意线程
/// 
/// MasterSlaveThreadManager：
/// 接口：
///     bool CanCommitTask();           //是否可以提交任务
///     void InterruptTask(Task _task); //中断任务
///     void AddTask(Task _task);       //添加的任务
/// 一般只要将你需要处理任务的函数传递进去，并在该函数根据当前任务所在的阶段，进行派发
/// 如：
///    protected void DealTask(Task _task) {
///     var _taskPhase = (ETaskPhase)_task.currentParseIdx;
///     switch (_taskPhase) {
///         case ETaskPhase.FirstAny:
///             OnDealTaskPreparePart(_task);
///             break;
///         case ETaskPhase.SecondMain:
///             OnDealTaskCommitPart(_task);
///             break;
///         default:
///             Debug.LogError("Task state invalid!" + _task.ToString());
///             break;
///     }
/// }
/// //建议 
/// 使用：Thread local storage 请 继承ThreadWorker后添加相应的成员 并重写ResetThreadLoacalStorage
/// 使用：Task local storage 请继承Task后添加相应的成员
/// 
/// 具体使用方式可以参考：TestThreadPatternMasterSlave
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
    public enum EThreadContext {
        /// <summary>
        /// 必须在主线程中运行，且必须做当前帧完成
        /// </summary>
        MainThreadAndInCurFrame,
        /// <summary>
        /// 必须在主线程中运行，但不一定是本帧
        /// </summary>
        MainThread,
        /// <summary>
        /// 可以运行在任意线程
        /// </summary>
        AnyThread,
    }


    public interface IThreadPattern {

        void DoUpdate(long _deltaTimeMs);
        /// <summary>
        /// 退出所有线程，并清理资源
        /// </summary>
        void DoDestroy();

        /// <summary>
        /// 中断任务
        /// </summary>
        /// <param name="_task"></param>
        void InterruptTask(Task _task);
        /// <summary>
        /// 是否可以提交任务
        /// </summary>
        /// <returns></returns>
        bool CanCommitTask();

        /// <summary>
        /// 添加的任务
        /// </summary>
        /// <param name="_task"></param>
        void AddTask(Task _task);
    }

    /// <summary>
    /// 多阶段提交的多线程任务管理器
    /// </summary>
    public class ThradPartternMasterSlaver : IThreadPattern {
        public delegate void FuncDealTask(Task _task);
        public delegate ThreadWorker FuncCreateWorker(ThradPartternMasterSlaver _mgr, int _idx);

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
        protected bool IsNeedToStop = false;
        public int GetCurFrameTaskCount() { return CurFrameTasks.Count; }
        public int GetMainThreadTaskCount() { return MainThreadTasks.Count; }
        public int GetAnyThreadTaskCount() { return AnyThreadTasks.Count; }


        /// <summary>
        /// 等待派发的任务 一定是在主线程
        /// </summary>
        List<Task> todoTasks = new List<Task>();
        HashSet<Task> todoTasksSet = new HashSet<Task>();
        /// <summary>
        /// 需要将在当前帧完成的任务
        /// </summary>
        List<Task> curFrameTodoTasks = new List<Task>();
        HashSet<Task> curFrameTodoTaskSet = new HashSet<Task>();

        protected List<ThreadWorker> threadWorkers;
        protected List<Thread> threads;
        /// <summary>
        /// 最大等待其他线程自动结束时间
        /// </summary>
        public int MaxWaitOtherThreadStopTimeMs = 12;

        protected FuncCreateWorker WorkCreateFunc;

        #region 每帧运行时间控制
        /// <summary>
        /// 每帧最多可以运行的毫秒数，用于控制帧率 (单位毫秒)
        /// </summary>
        public int MaxRunTimePreFrameInMs = 10;
        //获取当前的时间戳
        public float realtimeSinceStartupInMs { get { return Time.realtimeSinceStartup * 1000; } }

        public int TargetFrameDeltaTime = 33;//目标每帧跑多少毫秒
        protected float curFrameDeadline;//当前帧截至时间
        protected float lastFrameExcendTimeInMs;//上一帧超时时间
        //是否超时
        protected bool IsTimeOut() { return realtimeSinceStartupInMs > curFrameDeadline; }

        #endregion

        /// <summary>
        /// 处理任务的真正回调
        /// </summary>
        public FuncDealTask OnDealTaskEvent;
        /// <summary>
        /// 当前是否是运行在主线程中
        /// </summary>
        public bool IsInMainThread { get { return System.Threading.Thread.CurrentThread == mainThread; } }

        private bool isInited = false;
        public int CurFrameMaxRunMs;

        void BeginSample(string _msg) { if (!IsInMainThread) return; UnityEngine.Profiling.Profiler.BeginSample(_msg); }
        void EndSample() { if (!IsInMainThread) return; UnityEngine.Profiling.Profiler.EndSample(); }
        //主线程中的更新
        public void DoUpdate(long _deltaTimeMs) {
            if (!isInited)
                return;
            var _timePenaltyRate = Mathf.Min(1.0f, 1.0f * TargetFrameDeltaTime / _deltaTimeMs);//计算上一帧超时后的罚时，为了帧率更加的平衡
            CurFrameMaxRunMs = Mathf.Max((int)(MaxRunTimePreFrameInMs * _timePenaltyRate - lastFrameExcendTimeInMs), 0);
            curFrameDeadline = realtimeSinceStartupInMs + CurFrameMaxRunMs;
            //这一帧新提交的任务 需要进入队列
            BeginSample("DispatchWaitDispatchTasks");
            DispatchWaitDispatchTasks();
            EndSample();
            //处理必须在这帧完成的事件的回调
            BeginSample("DispatchWaitEnqueueTasks");
            DispatchWaitEnqueueTasks(AnyThreadTasks);//将等待队列中的可以在其他线程中运行的任务派发给其他的线程中
            EndSample();
            BeginSample("DealQueue CurFrameTasks");
            DealQueue(CurFrameTasks, mainThreadWorker, null, true);//处理本帧必须完成的任务，无时间限制
            EndSample();
            BeginSample("DealQueue MainThreadTasks");
            DealQueue(MainThreadTasks, mainThreadWorker, IsTimeOut, true);//处理必须在主线程中完成的任务，有时间限制
            EndSample();

            lastFrameExcendTimeInMs = realtimeSinceStartupInMs - curFrameDeadline;
        }
        public void DoStart(
            int _threadCount
            , int _maxTaskCount
            , FuncCreateWorker _WorkCreateFunc
            , FuncDealTask _OnDealTaskEvent
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
            OnDealTaskEvent = _OnDealTaskEvent;
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
            for (int _i = 0; _i < _threadCount; _i++) {
                threads[_i].Start();
            }
        }
        public bool IsStarted = false;
        public void DoDestroy() {
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
            var _initTime = Time.realtimeSinceStartup;
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
                if ((Time.realtimeSinceStartup - _initTime) > MaxWaitOtherThreadStopTimeMs * 0.001f) {
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
            return (todoTasks.Count + curFrameTodoTasks.Count) < MaxTaskCount * 3;
        }

        /// <summary>
        /// 添加的任务
        /// </summary>
        /// <param name="_task"></param>
        public void AddTask(Task _task) {
            if (!IsInMainThread) {
                Debug.LogError("任务的添加：必须在主线程");
                return;
            }
            if (_task.IsMustRunInCurFrame) {
                if (curFrameTodoTaskSet.Add(_task)) {
                    curFrameTodoTasks.Add(_task);
                }
            } else {
                if (todoTasksSet.Add(_task)) {
                    todoTasks.Add(_task);
                } else {
                    Debug.LogError("逻辑错误：添加重复的任务 " + _task.ToString());
                }
            }

        }

        #region Help Func

        //可能在任何线程回调
        protected void DealTask(Task _task) {
#if TEST_MAIN_THRED_BUG
            _task.workedThreadIds.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
#endif
            if (OnDealTaskEvent != null) {
                OnDealTaskEvent(_task);
            }
        }

        IBlockQueue<Task> GetQueueFromType(EThreadContext _queueType) {
            IBlockQueue<Task> _nextQueue = null;
            switch (_queueType) {
                case EThreadContext.MainThreadAndInCurFrame:
                    _nextQueue = CurFrameTasks;
                    break;
                case EThreadContext.MainThread:
                    _nextQueue = MainThreadTasks;
                    break;
                case EThreadContext.AnyThread:
                    _nextQueue = AnyThreadTasks;
                    break;
                default:
                    break;
            }
            return _nextQueue;
        }

        void DispatchWaitDispatchTasks() {
            DispathTasks(curFrameTodoTasks, curFrameTodoTaskSet, CurFrameTasks);
            DispathTasks(todoTasks, todoTasksSet, AnyThreadTasks);
        }

        private void DispathTasks(List<Task> _tasks, HashSet<Task> _taskSet, IBlockQueue<Task> _targetQueue) {
            var _count = _tasks.Count;
            for (int i = 0; i < _count; i++) {
                var _info = _tasks[i];
                //注意必须在当前帧执行的任务的添加一定是在主线程中，所以一定可以投递成功
                //其他的任务投递就不是必须全部投递成功，因为不需要一定在本帧完成
                if (_targetQueue.TryEnqueue(_info)) {
                    _tasks.RemoveAt(i);
                    _taskSet.Remove(_info);
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
            while (true) {
                Task _task = null;
                if (!_isInMainThread) {
                    _task = _todoQueue.Dequeue();
                } else {
                    if (!_todoQueue.TryDequeue(out _task)) {
                        if (_todoQueue == CurFrameTasks) {
                            if (!TryDequeueWaitEqueueTasks(_todoQueue, out _task)) {
                                return;
                            }
                        } else {
                            if (_FuncIsTimeOut())
                                return;
                            if (!TryDequeueWaitEqueueTasks(_todoQueue, out _task)) {
                                return;
                            }
                        }
                    }
                }
                _thread_worker.ResetThreadLoacalStorage();
                _task.threadLocalStorage = _thread_worker;
                if (!_task.IsFinished) {
                    //处理还未完成的任务
                    try {
                        DealTask(_task);
                        _task.MoveNextPhase();
                        //根据当前任务的状态派发到不同的队列中
                        var _queueType = _task.GetTargetQueueType();
                        var _nextQueue = GetQueueFromType(_queueType);
                        if (_isInMainThread) {
                            //为了防止阻塞
                            if (!_nextQueue.TryEnqueue(_task)) {
                                AddWaitEnqueueTask(_nextQueue, _task);
                            }
                        } else {
                            _nextQueue.Enqueue(_task);
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