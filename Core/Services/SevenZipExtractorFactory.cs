using AutoUnpackTool.Core.Configuration;

namespace AutoUnpackTool.Core.Services
{
    /// <summary>
    /// SevenZipExtractor 工厂：SevenZipPath 可在运行期被设置对话框修改，故不缓存实例，
    /// 每次 Create() 读取最新路径新建，消除调用点散落的 new 与路径缓存失效风险。
    /// </summary>
    public sealed class SevenZipExtractorFactory
    {
        private readonly AppSettings _settings;
        private readonly SevenZipRunner _runner;

        public SevenZipExtractorFactory(AppSettings settings, SevenZipRunner runner)
        {
            _settings = settings ?? throw new System.ArgumentNullException(nameof(settings));
            _runner = runner ?? throw new System.ArgumentNullException(nameof(runner));
        }

        public SevenZipExtractor Create() => new(_settings.SevenZipPath, _runner);
    }
}
