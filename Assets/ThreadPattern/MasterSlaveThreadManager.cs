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
    /// Master Slave 模式的线程管理器
    /// </summary>
    public class MasterSlaveThreadManager : MutilPhaseThreadTaskManager {
        protected IBlockQueue<Task> mainThreadTaskQueue;
        protected List<ThreadWorker> threadingRenderWorkerList;
        protected List<Thread> threadingList;
        private int threadCount;
        protected bool alsoRunInMainThread = false;
        protected int mainThreadId;
        protected System.Func<MasterSlaveThreadManager, int, ThreadWorker> WorkCreateFunc;
        public MasterSlaveThreadManager(int _threadCount, int _maxTaskCount, System.Func<MasterSlaveThreadManager, int, ThreadWorker> _WorkCreateFunc, bool _alsoRunInMainThread)
            : base(_maxTaskCount) {
            this.alsoRunInMainThread = _alsoRunInMainThread;
            this.threadCount = _threadCount;
            WorkCreateFunc = _WorkCreateFunc;
            mainThreadWorker = _WorkCreateFunc(this, -1);
            mainThreadTaskQueue = CreateBlockQueue(MaxTaskCount() * 2);
            threadingRenderWorkerList = new List<ThreadWorker>();
            threadingList = new List<Thread>();
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }
        //是否在主线程中
        public bool IsInMainThread { get { return System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId; } }
        public int GetSecondPassTaskCount() { return this.mainThreadTaskQueue.Count; }

        public virtual bool CanCommitTask() {
            return GetAnyThreadTaskCount() < MaxTaskCount
                && GetSecondPassTaskCount() < MaxTaskCount * 2;
        }

        public override void Start() {
            for (int _i = 0; _i < threadCount; ++_i) {
                //每个线程都分配避免互相之间锁 而挂起
                var _worker = WorkCreateFunc(this, _i);
                Thread _thread = new Thread(_worker.Run);
                _thread.IsBackground = true;
                _thread.Start();
                threadingRenderWorkerList.Add(_worker);
                threadingList.Add(_thread);
            }
        }
        public override void OnDestroy() {
            //1 中断线程
            for (int _i = 0; _i < threadingRenderWorkerList.Count; ++_i) {
                threadingRenderWorkerList[_i].NotifyToStop();
            }
            for (int _i = 0; _i < threadingList.Count; ++_i) {
                try {
                    threadingList[_i].Interrupt();
                } catch (System.Threading.ThreadInterruptedException) {
                    Debug.LogWarning("Interrupted whilst waiting for worker to die");
                }
            }

            int maxWaitStopTimeMs = 12;//最多等待结束时间
            var _initTime = DateTime.Now;
            while (true) {
                bool _isFinishedAll = true;
                for (int _i = 0; _i < threadingRenderWorkerList.Count; ++_i) {
                    if (!threadingRenderWorkerList[_i].IsFinisedRun) {
                        _isFinishedAll = false;
                    }
                }
                if (_isFinishedAll) {
                    break;
                }
                Thread.Sleep(1);
                if ((_initTime - DateTime.Now).TotalMilliseconds > maxWaitStopTimeMs) {
                    break;
                }
            }

            //强制结束
            for (int _i = 0; _i < threadingRenderWorkerList.Count; ++_i) {
                if (!threadingRenderWorkerList[_i].IsFinisedRun) {
                    threadingList[_i].Abort();
                }
            }

            threadingList.Clear();
            //2 删除未完成的task        
            while (!AnyThreadTasks.IsEmpty) {
                Task _task = null;
                AnyThreadTasks.TryDequeue(out _task);
                if (_task != null && _task.OnInterruptEvent != null) {
                    _task.OnInterruptEvent(_task);
                }
            }
            while (!mainThreadTaskQueue.IsEmpty) {
                Task _task = null;
                mainThreadTaskQueue.TryDequeue(out _task);
                if (_task != null && _task.OnInterruptEvent != null) {
                    _task.OnInterruptEvent(_task);
                }
            }
            //Finish Task 也要中断
            while (!finishedTasks.IsEmpty) {
                Task _task = null;
                finishedTasks.TryDequeue(out _task);
                if (_task != null && _task.OnInterruptEvent != null) {
                    _task.OnInterruptEvent(_task);
                }
            }

            while (!interruptTasks.IsEmpty) {
                Task _task = null;
                interruptTasks.TryDequeue(out _task);
                if (_task != null && _task.OnInterruptEvent != null) {
                    _task.OnInterruptEvent(_task);
                }
            }
            //3 通知中断事件
            Debug.LogError("Finish Destroy");
        }
        public void InterruptTask(Task _task) {
            _task.IsInterrupted = true;
        }
        protected override void ProcessTasks(out Task _curTask) {
            NoBlockingProcessTasks(mainThreadTaskQueue, NoBlockingProcessTaskCommitPart, out _curTask, mainThreadWorker);
            if (this.threadCount <= 0 || alsoRunInMainThread) {
                //在只有主线程情况下需要主线程自己处理这些任务
                NoBlockingProcessTasks(AnyThreadTasks, NoBlockingProcessTaskPreparePart, out _curTask, mainThreadWorker);
                NoBlockingProcessTasks(mainThreadTaskQueue, NoBlockingProcessTaskCommitPart, out _curTask, mainThreadWorker);
            }
        }

        public void BlockingProcessTaskPreparePart(Task _task) {
            DealTaskPreparePart(_task);
            //提交到主线程中去
            mainThreadTaskQueue.Enqueue(_task);
        }
        public void NoBlockingProcessTaskPreparePart(Task _task) {
            Task _toEnqueueTask = _task;
            DealTaskPreparePart(_toEnqueueTask);
#if TEST_MAIN_THRED_BUG
            _task.isDealInMainThread = true;
#endif
            //提交到主线程中去 
            //everything should not block thread
            while (!mainThreadTaskQueue.TryEnqueue(_task)) {
                Task _curTask = null;
                try {
                    //将位置给空出来 让主线程队列可以Enqueue
                    NoBlockingProcessTasks(mainThreadTaskQueue, NoBlockingProcessTaskCommitPart, out _curTask, mainThreadWorker);
                } catch (System.Threading.ThreadInterruptedException) {
                    //正常现象
                    _curTask.IsInterrupted = true;
                    NoBlockingEnqueue(_curTask);
                    Debug.LogError("Logic Error Main Thread was interrupted");
                } catch (System.Exception _e) {
                    _curTask.Exception = _e;
                    NoBlockingEnqueue(_curTask);
                }
            }
        }
        //处理第一阶段任务 可能在任何线程
        protected virtual void DealTaskPreparePart(Task _task) {
            ++_task.dealNum;
#if DEBUG_THREAD_PHASE_ID
            _task.workedThreadIds.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
#endif
            OnDealTaskPreparePart(_task);
        }
        //处理最后一阶段任务 在主线程
        protected override void DealTask(Task _task) {
            ++_task.dealNum;
#if DEBUG_THREAD_PHASE_ID
            _task.workedThreadIds.Add(System.Threading.Thread.CurrentThread.ManagedThreadId);
#endif
            OnDealTaskCommitPart(_task);
        }
        //可能在任何线程回调
        protected virtual void OnDealTaskPreparePart(Task _task) { }
        //在主线程回调
        protected virtual void OnDealTaskCommitPart(Task _task) { }
    }

}