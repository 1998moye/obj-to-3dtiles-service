namespace ModelConversion.Service.Application;

// 内存看门狗达到硬水位后主动终止转换进程时抛出；与普通转换失败区分，便于上层诊断。
public sealed class MemoryProtectionException(string message) : Exception(message);
