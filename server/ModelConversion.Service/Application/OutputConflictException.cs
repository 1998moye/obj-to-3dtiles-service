namespace ModelConversion.Service.Application;

public sealed class OutputConflictException(string message) : Exception(message);
