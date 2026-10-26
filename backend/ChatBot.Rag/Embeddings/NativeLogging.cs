using LLama.Native;

namespace ChatBot.Rag.Embeddings;

// llama.cpp prints several hundred lines per model load and per embedding context. The
// console tools only want errors. Must run before the first native call.
public static class NativeLogging
{
    public static void ErrorsOnly()
    {
        NativeLibraryConfig.All.WithLogCallback((level, message) =>
        {
            if (level == LLamaLogLevel.Error) Console.Error.Write(message);
        });
    }
}
