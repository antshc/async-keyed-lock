# async-keyed-lock

```
TaskCompletionSource: 
“I’ll give you a Task now, and I will decide later when it finishes, fails, or gets canceled.”
```

```csharp
var tcs = new TaskCompletionSource<int>();

Task<int> task = tcs.Task; // hand this to consumers

// later, from ANY thread:
tcs.SetResult(42); // TrySetResult         // completes the task successfully
// or:
tcs.SetException(ex);                      // faults the task
// or:
tcs.SetCanceled();                         // cancels the task
```