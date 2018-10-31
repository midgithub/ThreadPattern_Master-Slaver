# ThreadPattern_Master-Slaver

多线程调用框架：
功能：
> 1.支持每帧指定最大运行时间
2.支持3种线程运行环境：主线程且必须在当前帧完成，必须在主线程完成，可以在任意线程中执行
3.支持可指定同一个任务可在多阶段的在指定线程环境中执行，如第一阶段可以在任意线程中执行，最后阶段必须在主线程中执行
4.支持时间分摊，如上一帧执行时间太长，这以帧的执行时间将会缩减，避免卡的情况
5.支持任务中断

任务完成的时候可以配置必须在主线程中回调。(也可以在任意线程回调，但不建议这样做)

使用方式：
Task：
如果希望自己的Task 可以被多遍遍历 可以重载 
```c
    MaxPhaseNum()//返回希望被处理的次数
    GetCurParseThradContextType() //返回该任务第多少阶段需要在什么类型的线程中执行
        MainThreadAndInCurFrame,//必须在主线程中运行，且必须做当前帧完成
        MainThread,//必须在主线程中运行，但不一定是本帧
        AnyThread,//可以运行在任意线程
```

MasterSlaveThreadManager：
接口：

```c
    bool CanCommitTask();           //是否可以提交任务
    void InterruptTask(Task _task); //中断任务
    void AddTask(Task _task);       //添加的任务

```

一般只要将你需要处理任务的函数传递进去，并在该函数根据当前任务所在的阶段，进行派发
如：

```c
   protected void DealTask(Task _task) {
    var _taskPhase = (ETaskPhase)_task.currentParseIdx;
    switch (_taskPhase) {
        case ETaskPhase.FirstAny:
            OnDealTaskPreparePart(_task);
            break;
        case ETaskPhase.SecondMain:
            OnDealTaskCommitPart(_task);
            break;
        default:
            Debug.LogError("Task state invalid!" + _task.ToString());
            break;
    }
}
```

//建议 
使用：Thread local storage 请 继承ThreadWorker后添加相应的成员 并重写ResetThreadLoacalStorage
使用：Task local storage 请继承Task后添加相应的成员

具体使用方式可以参考：TestThreadPatternMasterSlave