/// <summary>
/// jiepengtan:20180726
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

#define TEST_MAIN_THRED_BUG // 测试主线程参与是否会导致bug
#define DEBUG_THREAD_PHASE_ID //开启线程阶段调试
using UnityEngine;
using System.Collections.Generic;
using System.Threading;
using System;


namespace FM.Threading {

    public interface IBlockQueue<T> {
        bool TryDequeue(out T _ret);
        bool TryEnqueue(T _ret);
        bool TryEnqueue(T _ret, int _ms);
        T Dequeue();
        void Enqueue(T _ret);
        bool IsEmpty { get; }
        int Count { get; }
    }

    public interface IQueue<T> {
        T Dequeue();
        void Enqueue(T _ret);
        int Count { get; }
    }

    public class HeapPriorityQueue<T> : IQueue<T> where T : IComparable<T> {
        List<T> array = new List<T>();
        public void Enqueue(T _item) {
            int _n = array.Count;
            array.Add(_item);
            while (_n != 0) {
                int _p = _n / 2;    // This is the 'parent' of this item
                if (array[_n].CompareTo(array[_p]) >= 0) break;  // Item >= parent
                T _tmp = array[_n]; array[_n] = array[_p]; array[_p] = _tmp; // Swap item and parent
                _n = _p;            // And continue
            }
        }
        public T Dequeue() {
            T _val = array[0];
            int _nMax = array.Count - 1;
            array[0] = array[_nMax]; array.RemoveAt(_nMax);  // Move the last element to the top
            int _p = 0;
            while (true) {
                int _c = _p * 2; if (_c >= _nMax) break;
                if (_c + 1 < _nMax && array[_c + 1].CompareTo(array[_c]) < 0) _c++;
                if (array[_p].CompareTo(array[_c]) <= 0) break;
                T _tmp = array[_p]; array[_p] = array[_c]; array[_c] = _tmp;
                _p = _c;
            }
            return _val;
        }
        public T Peek() {
            return array[0];
        }
        public void Clear() { array.Clear(); }
        public int Count {
            get { return array.Count; }
        }
        public bool Empty {
            get { return array.Count == 0; }
        }
    }


    public class QueueWrap<T> : IQueue<T> {
        private Queue<T> _proxy = new Queue<T>();
        public void Enqueue(T _ret) { _proxy.Enqueue(_ret); }
        public T Dequeue() { return _proxy.Dequeue(); }
        public int Count { get { return _proxy.Count; } }
    }

    public class BlockingQueue<T> : IBlockQueue<T> {
        private readonly IQueue<T> queue;
        private readonly int maxSize;
        public BlockingQueue(IQueue<T> _queue, int _maxSize) {
            this.maxSize = _maxSize;
            queue = _queue;
        }
        public bool IsEmpty {
            get {
                lock (queue) {
                    return queue.Count == 0;
                }
            }
        }
        public int Count {
            get {
                lock (queue) {
                    return queue.Count;
                }
            }
        }
        //是否正在关闭
        bool closing = false;
        public void Close() {
            lock (queue) {
                closing = true;
                Monitor.PulseAll(queue);
            }
        }
        public void Enqueue(T item) {
            lock (queue) {
                while (queue.Count >= maxSize) {
                    Monitor.Wait(queue);
                    if (closing) {
                        return;
                    }
                }
                queue.Enqueue(item);
                if (queue.Count == 1) {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(queue);
                }
            }
        }
        public T Dequeue() {
            lock (queue) {
                while (queue.Count == 0) {
                    Monitor.Wait(queue);
                    if (closing) {
                        return default(T);
                    }
                }
                T _item = queue.Dequeue();
                if (queue.Count == maxSize - 1) {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(queue);
                }
                return _item;
            }
        }

        public bool TryDequeue(out T _val) {
            lock (queue) {
                if (queue.Count == 0) {
                    _val = default(T);
                    return false;
                }
                _val = queue.Dequeue();
                if (queue.Count == maxSize - 1) {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }
        public bool TryDequeue(out T _val, int _durationMs) {
            DateTime _deadline = DateTime.Now.AddMilliseconds(_durationMs);
            var _remainMs = _durationMs;
            lock (queue) {
                while (queue.Count == 0 && _remainMs > 0) {
                    if (closing) {
                        _val = default(T);
                        return false;
                    }
                    Monitor.Wait(queue, _remainMs);
                    _remainMs = (int)(_deadline - DateTime.Now).TotalMilliseconds;
                }
                if (queue.Count == 0) {
                    _val = default(T);
                    return false;
                }
                _val = queue.Dequeue();
                if (queue.Count == maxSize - 1) {
                    // wake up any blocked enqueue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }
        public bool TryEnqueue(T _item) {
            lock (queue) {
                if (queue.Count >= maxSize) {
                    return false;
                }
                queue.Enqueue(_item);
                if (queue.Count == 1) {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }

        public bool TryEnqueue(T _item, int _durationMs) {
            DateTime deadline = DateTime.Now.AddMilliseconds(_durationMs);
            var _remainMs = _durationMs;
            lock (queue) {
                while (queue.Count >= maxSize && _remainMs > 0) {
                    Monitor.Wait(queue, _remainMs);
                    _remainMs = (int)(deadline - DateTime.Now).TotalMilliseconds;
                }
                if (queue.Count >= maxSize) {
                    _item = default(T);
                    return false;
                }
                queue.Enqueue(_item);
                if (queue.Count == 1) {
                    // wake up any blocked dequeue
                    Monitor.PulseAll(queue);
                }
                return true;
            }
        }
    }

    public class Task : System.IComparable<Task> {
        public ThreadWorker threadLocalStorage;
        public Task parent;
        public System.Exception exception;
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

        public volatile bool IsFinished;
        public volatile bool IsInterrupted;


        public System.Action<Task> OnFinishedEvent;//可能失败  可能异常  可能成功 三种情况
        public System.Action<Task> OnInterruptEvent;

        public virtual void OnInterrupted() {
            if (OnInterruptEvent != null) OnInterruptEvent(this);
        }
        public virtual void OnFinished() {
            if (OnFinishedEvent != null) OnFinishedEvent(this);
        }

        public bool HasNextPhase() { return (currentParseIdx + 1) < MaxPhaseNum(); }
        public void ChangeToNextPhase() { ++currentParseIdx; }

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
        private bool isStop = false;
        /// <summary>
        /// reset thread local storage
        /// </summary>
        public virtual void ResetThreadLoacalStorage() { }
        public void NotifyToStop() { isStop = true; }

        public bool IsFinisedRun = false;
        public void Run() {
            while (!isStop) {
                Task _task = master.mutilThreadsTaskQueue.Dequeue();
                if (_task.IsInterrupted) {
                    master.NoBlockingNotifyTaskInterrupt(_task);//
                    continue;
                }

                try {
                    ResetThreadLoacalStorage();
                    _task.threadLocalStorage = this;
                    master.BlockingProcessTaskPreparePart(_task);
                } catch (System.Threading.ThreadInterruptedException) {
                    //正常现象
                    _task.IsInterrupted = true;
                    master.NoBlockingNotifyTaskInterrupt(_task);//here want to be destory thread so here should not be block 
                    Debug.Log("Interrupt Stop");
                    IsFinisedRun = true;
                    return;
                } catch (System.Exception _e) {
                    _task.exception = _e;
                    master.BlockingNotifyTaskFinished(_task);
                }
            }
            IsFinisedRun = true;
            Debug.Log("Auto Stop");
        }

    }
    /// <summary>
    /// 多阶段提交的多线程任务管理器
    /// </summary>
    public class MutilPhaseThreadTaskManager {
        protected IBlockQueue<Task> finishedTasks;// 已经完成的任务
        protected IBlockQueue<Task> interruptTasks;// 被中断的任务

        public IBlockQueue<Task> mutilThreadsTaskQueue;//多线程队列 所有的这些任务可能在任何线程中执行
        protected ThreadWorker mainThreadWorker;//主线程自己的变量

        protected int maxTaskCount = 0;//最大任务数量
        const int MAX_ENQUEUE_WAIT_TIME_MS = 3000;//最大等待时间
        protected virtual IBlockQueue<Task> CreateBlockQueue(int count) {
            return new BlockingQueue<Task>(new HeapPriorityQueue<Task>(), count);
        }
        public MutilPhaseThreadTaskManager(int _maxTaskCount) {
            this.maxTaskCount = _maxTaskCount;
            finishedTasks = CreateBlockQueue(GetMaxTaskCount() * 2);
            interruptTasks = CreateBlockQueue(GetMaxTaskCount() * 2);
            mutilThreadsTaskQueue = CreateBlockQueue(GetMaxTaskCount());
        }
        //设置初始时间戳
        protected virtual void SetTimer(long _maxRunTimeMs) { }
        //同时最大任务数量
        protected int GetMaxTaskCount() { return maxTaskCount; }
        //正在处理的任务数量
        public int GetProcessingTaskCount() { return mutilThreadsTaskQueue.Count; }
        public int GetInterruptTaskCount() { return interruptTasks.Count; }
        public int GetFinishedTaskCount() { return finishedTasks.Count; }
        //获取当前的时间戳
        protected long GetCurrentTicks() { return System.DateTime.Now.Ticks; }
        //是否超时
        protected virtual bool IsTimeOut() { return false; }
        //处理最后一阶段任务
        protected virtual void DealTaskCommitPart(Task _task) { }
        public virtual void Start() { }
        public virtual void OnDestroy() { CleanTodoThreadingTasks(); }
        public void Update(long _maxRunTimeMs) {
            SetTimer(_maxRunTimeMs);
            Task _task = null;
            try {
                DealTaskEvents();
                ProcessTasks(out _task);
                _task = null;
                DealTaskEvents();
            } catch (System.Threading.ThreadInterruptedException) {
                //正常现象
                if (_task != null) {
                    NoBlockingNotifyTaskInterrupt(_task);
                }
                Debug.LogError("Logic Error Main Thread should not be interrupted");
            } catch (System.Exception _e) {
                if (_task != null) {
                    _task.exception = _e;
                    NoBlockingNotifyTaskFinished(_task);
                }
                Debug.LogError(_e);
            }
        }


        private void DealTaskEvents() {
            Task _task = null;
            while (finishedTasks.TryDequeue(out _task)) {
                if (_task.IsInterrupted) {
                    _task.OnInterrupted();
                } else {
                    _task.OnFinished();
                }
                if (IsTimeOut()) {
                    break;
                }
            }
            while (interruptTasks.TryDequeue(out _task)) {
                _task.OnInterrupted();
                if (IsTimeOut()) {
                    break;
                }
            }
        }

        protected virtual void ProcessTasks(out Task _curTask) {
            NoBlockingProcessTasks(mutilThreadsTaskQueue, NoBlockingProcessTaskCommitPart, out _curTask, mainThreadWorker);
        }

        protected void NoBlockingProcessTasks(IBlockQueue<Task> _queue, System.Action<Task> _TaskDealFunc, out Task _task, ThreadWorker _thread_worker) {
            _task = null;
            if (IsTimeOut()) {
                _task = null;
                return;
            }
            while (_queue.TryDequeue(out _task)) {
                if (_task.IsInterrupted) {
                    NoBlockingNotifyTaskInterrupt(_task);
                    continue;
                }
                try {
                    _thread_worker.ResetThreadLoacalStorage();
                    _task.threadLocalStorage = _thread_worker;
                    _TaskDealFunc(_task);
                } catch (System.Threading.ThreadInterruptedException) {
                    //正常现象
                    _task.IsInterrupted = true;
                    NoBlockingNotifyTaskInterrupt(_task);
                } catch (System.Exception _e) {
                    _task.exception = _e;
                    NoBlockingNotifyTaskFinished(_task);
                }
                if (IsTimeOut()) {
                    break;
                }
            }
        }


        protected void NoBlockingProcessTaskCommitPart(Task _task) {
            if (CheckInterrupt(_task)) { NoBlockingNotifyTaskInterrupt(_task); return; }
            DealTaskCommitPart(_task);
            if (CheckInterrupt(_task)) { NoBlockingNotifyTaskInterrupt(_task); return; }
            if (_task.HasNextPhase()) {
                _task.ChangeToNextPhase();
                while (!mutilThreadsTaskQueue.TryEnqueue(_task, MAX_ENQUEUE_WAIT_TIME_MS)) { Debug.LogError("Logic Error:Try Enqueue Wait to long"); }
            } else {
                NoBlockingNotifyTaskFinished(_task);
            }
        }

        private bool CheckInterrupt(Task _task) {
            Task _temp_task = _task;
            bool _IsInterrupted = false;
            while (_temp_task != null) {
                if (_temp_task.IsInterrupted) {
                    _IsInterrupted = true;
                    break;
                }
                _temp_task = _temp_task.parent;
            }
            return _IsInterrupted;
        }
        public void BlockingNotifyTaskFinished(Task _task) { _task.IsFinished = true; finishedTasks.Enqueue(_task); }
        public void NoBlockingNotifyTaskInterrupt(Task _task) { while (!interruptTasks.TryEnqueue(_task, MAX_ENQUEUE_WAIT_TIME_MS)) { Debug.LogError("Logic Error:Try Enqueue Wait to long"); }; }
        public void NoBlockingNotifyTaskFinished(Task _task) { _task.IsFinished = true; while (!finishedTasks.TryEnqueue(_task, MAX_ENQUEUE_WAIT_TIME_MS)) { Debug.LogError("Logic Error:Try Enqueue Wait to long"); }; }
        protected void CleanTodoThreadingTasks() {
            while (!mutilThreadsTaskQueue.IsEmpty) {
                Task _task = null;
                mutilThreadsTaskQueue.TryDequeue(out _task);
                if (_task != null && _task.OnInterruptEvent != null) {
                    _task.OnInterruptEvent(_task);
                }
            }
        }
    }
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
            mainThreadTaskQueue = CreateBlockQueue(GetMaxTaskCount() * 2);
            threadingRenderWorkerList = new List<ThreadWorker>();
            threadingList = new List<Thread>();
            mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }
        //是否在主线程中
        public bool IsInMainThread { get { return System.Threading.Thread.CurrentThread.ManagedThreadId == mainThreadId; } }
        public int GetSecondPassTaskCount() { return this.mainThreadTaskQueue.Count; }

        public virtual bool CanCommitTask() {
            return GetProcessingTaskCount() < maxTaskCount
                && GetSecondPassTaskCount() < maxTaskCount * 2;
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
            while (!mutilThreadsTaskQueue.IsEmpty) {
                Task _task = null;
                mutilThreadsTaskQueue.TryDequeue(out _task);
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
                NoBlockingProcessTasks(mutilThreadsTaskQueue, NoBlockingProcessTaskPreparePart, out _curTask, mainThreadWorker);
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
                    NoBlockingNotifyTaskInterrupt(_curTask);
                    Debug.LogError("Logic Error Main Thread was interrupted");
                } catch (System.Exception _e) {
                    _curTask.exception = _e;
                    NoBlockingNotifyTaskFinished(_curTask);
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
        protected override void DealTaskCommitPart(Task _task) {
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