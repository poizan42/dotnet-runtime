using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO
{
    public abstract partial class Stream
    {
        protected internal bool SupportsCancelSynchronousIo { get; set; }
        private static readonly Action<object?> _onCancelReadWriteDelegate = OnCancelReadWrite;
        private static readonly Func<object?, int> _syncIOCancelableReadWriteTaskReadDelegate = SyncIOCancelableReadWriteTaskRead;
        private static readonly Func<object?, int> _syncIOCancelableReadWriteTaskWriteDelegate = SyncIOCancelableReadWriteTaskWrite;

        private static int SyncIOCancelableReadWriteTaskRead(object? _)
        {
            // The ReadWriteTask stores all of the parameters to pass to Read.
            // As we're currently inside of it, we can get the current task
            // and grab the parameters from it.
            var thisTask = Task.InternalCurrent as ReadWriteTask;
            Debug.Assert(thisTask != null && thisTask._stream != null && thisTask._buffer != null,
                "Inside ReadWriteTask, InternalCurrent should be the ReadWriteTask, and stream and buffer should be set");

            CancellationToken ct = thisTask.CancellationToken;
            CancellationTokenRegistration ctReg = default; // The default initialized CTR does nothing when disposed (_node is null)
            try
            {
                thisTask._osThreadId = Thread.CurrentOSThreadId;
                ctReg = ct.UnsafeRegister(_onCancelReadWriteDelegate, thisTask);
                // Do the Read and return the number of bytes read
                return thisTask._stream.Read(thisTask._buffer, thisTask._offset, thisTask._count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw; // Not reached
            }
            finally
            {
                thisTask._osThreadId = 0;
                // CancellationTokenRegistration.Dispose will wait for the cancellation callback if it is currently running.
                // So after this line we should be guarenteed that it is either done calling CancelSynchronousIo or _cancelTask has been spun up.
                ctReg.Dispose();
                thisTask._cancelTask?.Wait();
                // If this implementation is part of Begin/EndXx, then the EndXx method will handle
                // finishing the async operation.  However, if this is part of XxAsync, then there won't
                // be an end method, and this task is responsible for cleaning up.
                if (!thisTask._apm)
                {
                    thisTask._stream.FinishTrackingAsyncOperation();
                }

                thisTask.ClearBeginState(); // just to help alleviate some memory pressure
            }
        }

        private static int SyncIOCancelableReadWriteTaskWrite(object? _)
        {
            // The ReadWriteTask stores all of the parameters to pass to Write.
            // As we're currently inside of it, we can get the current task
            // and grab the parameters from it.
            var thisTask = Task.InternalCurrent as ReadWriteTask;
            Debug.Assert(thisTask != null && thisTask._stream != null && thisTask._buffer != null,
                "Inside ReadWriteTask, InternalCurrent should be the ReadWriteTask, and stream and buffer should be set");

            CancellationToken ct = thisTask.CancellationToken;
            CancellationTokenRegistration ctReg = default; // The default initialized CTR does nothing when disposed (_node is null)
            try
            {
                thisTask._osThreadId = Thread.CurrentOSThreadId;
                ctReg = ct.UnsafeRegister(_onCancelReadWriteDelegate, thisTask);
                // Do the Write
                thisTask._stream.Write(thisTask._buffer, thisTask._offset, thisTask._count);
                return 0; // not used, but signature requires a value be returned
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw; // Not reached
            }
            finally
            {
                thisTask._osThreadId = 0;
                // CancellationTokenRegistration.Dispose will wait for the cancellation callback if it is currently running.
                // So after this line we should be guarenteed that it is either done calling CancelSynchronousIo or _cancelTask has been spun up.
                ctReg.Dispose();
                thisTask._cancelTask?.Wait();
                // If this implementation is part of Begin/EndXx, then the EndXx method will handle
                // finishing the async operation.  However, if this is part of XxAsync, then there won't
                // be an end method, and this task is responsible for cleaning up.
                if (!thisTask._apm)
                {
                    thisTask._stream.FinishTrackingAsyncOperation();
                }

                thisTask.ClearBeginState(); // just to help alleviate some memory pressure
            }
        }

        private static void OnCancelReadWrite(object? obj)
        {
            const int THREAD_TERMINATE = 0x0001;

            var thisTask = obj as ReadWriteTask;
            Debug.Assert(thisTask != null, "Inside OnCancelReadWrite, obj should be ReadWriteTask");
            SpinWait spinWait = default;
            int threadId = (int)thisTask._osThreadId;
            if (threadId == 0)
                return;
            IntPtr hThread = Interop.Kernel32.OpenThread_IntPtr(THREAD_TERMINATE, false, threadId);
            // Throw on failure or bail out silently? We might fail if Cancel is called under impersonation or someone is playing silly games with the thread ACL.
            Debug.Assert(hThread != IntPtr.Zero, "Failed opening thread", "OnCancelReadWrite failed opening thread id {0}: 0x{1:X8}", threadId, Marshal.GetLastWin32Error());
            bool closeHandle = true;
            try
            {
                bool syncIoCancelled = false;
                // We may hit a race between _osThreadId being set and the syscall starting,
                // so repeat trying to cancel until something is cancelled or the stream operation has completed.
                while (thisTask._osThreadId != 0)
                {
                    syncIoCancelled = Interop.Kernel32.CancelSynchronousIo(hThread);
                    int err = syncIoCancelled ? 0 : Marshal.GetLastWin32Error();
                    // Handling of unexpected error?
                    Debug.Assert(syncIoCancelled || err == Interop.Errors.ERROR_NOT_FOUND, "Canceling synchronous IO failed", "CancelSynchronousIo failed with error code 0x{0:X8}", err);
                    if (syncIoCancelled)
                        break;
                    // We don't want to block this thread for too long, so complete the operation on a worker thread if we are going to do a context switch anyways.
                    if (spinWait.NextSpinWillYield)
                        break;
                    spinWait.SpinOnce();
                }
                if (!syncIoCancelled)
                {
                    closeHandle = false;
                    // This allocates a new delegate and closure object, is performance important here?
                    thisTask._cancelTask = new Task(() =>
                    {
                        // Should this have a timeout? Reading/writing synchronously from/to a socket with an LSP might be stuck in usermode,
                        // CancelSynchronousIo might not work in that case. Also detouring etc.
                        try
                        {
                            while (thisTask._osThreadId != 0)
                            {
                                syncIoCancelled = Interop.Kernel32.CancelSynchronousIo(hThread);
                                int err = syncIoCancelled ? 0 : Marshal.GetLastWin32Error();
                                // Handling of unexpected error?
                                Debug.Assert(syncIoCancelled || err == Interop.Errors.ERROR_NOT_FOUND, "Canceling synchronous IO failed", "CancelSynchronousIo failed with error code 0x{0:X8}", err);
                                if (syncIoCancelled)
                                    break;
                                spinWait.SpinOnce();
                            }
                        }
                        finally
                        {
                            Interop.Kernel32.CloseHandle(hThread);
                        }
                    });
                    thisTask._cancelTask.Start(TaskScheduler.Default);
                }
            }
            finally
            {
                if (closeHandle)
                    Interop.Kernel32.CloseHandle(hThread);
            }
        }
    }
}
