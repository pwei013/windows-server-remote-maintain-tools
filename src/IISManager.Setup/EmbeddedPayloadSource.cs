using System.Reflection;
using IISManager.Setup.Core;

namespace IISManager.Setup;

public sealed class EmbeddedPayloadSource : ISetupPayloadSource
{
    public ISetupPayload Open(SetupComponent component)
    {
        if (!Enum.IsDefined(component)) throw new SetupException("COMPONENT_INVALID", "组件无效。");
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IISManager.Setup.Payload." + component + ".zip")
            ?? throw new SetupException("PAYLOAD_MISSING", "安装器缺少组件资源，请使用完整安装包。");
        return new PayloadArchive(stream, component);
    }
}
