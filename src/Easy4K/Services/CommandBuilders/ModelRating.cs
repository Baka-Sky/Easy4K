namespace Easy4K.Services.CommandBuilders;

/// <summary>推荐星级（1-5）渲染成 ★★★☆☆。超分与补帧共用同一套渲染，保证两边格式一致。</summary>
public static class ModelRating
{
    /// <summary>5 分制 → ★★★☆☆；越界自动夹到 0..5</summary>
    public static string Stars(int value)
    {
        var v = Math.Clamp(value, 0, 5);
        return new string('★', v) + new string('☆', 5 - v);
    }
}
