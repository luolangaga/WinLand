namespace HelloLib;

public sealed class HelloModel
{
    private int _count;

    public string Describe()
    {
        _count++;
        return $"HelloLib 私有依赖已加载（调用 {_count} 次）";
    }
}
