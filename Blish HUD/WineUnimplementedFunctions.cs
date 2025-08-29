using dnlib.DotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace WineUnimplementedFunctions {
    /// <summary>
    /// Used to stub certain functions we don't care about that are unimplemented in WINE so they just do nothing instead of crashing.
    /// </summary>
    public static class WineBlishModuleLoader {

        private static readonly string[] _redirectNamespaces = new[]
    {
        "Windows.UI.Notifications",
    };

        /// <summary>
        /// This function redirects found references to some WinRT types to custom stubbed ones.
        /// This way things won't crash when DotNet sees those as unimplemented because of wine
        /// </summary>
        /// <param name="asmBytes"></param>
        /// <returns></returns>
        public static byte[] RedirectRefs(byte[] asmBytes) {

            //Only do this for DLLs with unimplemented functions
            if (!ReferencesUnimplemented(asmBytes))
                return asmBytes;

            using var ms = new MemoryStream(asmBytes);
            var mod = ModuleDefMD.Load(ms);


            var asmName = Assembly.GetExecutingAssembly().GetName().Name;
            var asmRef = new AssemblyRefUser(new AssemblyNameInfo(asmName));

            foreach (var tr in mod.GetTypeRefs()) {
                if (!string.IsNullOrEmpty(tr.Namespace?.String) && _redirectNamespaces.Contains(tr.Namespace.String)) { 
                    tr.ResolutionScope = asmRef;
                }
            }

            using var outStream = new MemoryStream();
            mod.Write(outStream);
            return outStream.ToArray();
        }


        /// <summary>
        /// Quick check to see if the DLL needs to be patched
        /// </summary>
        /// <param name="asmBytes"></param>
        /// <returns></returns>
        private static bool ReferencesUnimplemented(byte[] asmBytes) {
            using var ms = new MemoryStream(asmBytes);
            var mod = ModuleDefMD.Load(ms);

            foreach (var tr in mod.GetTypeRefs()) {
                if (!string.IsNullOrEmpty(tr.Namespace?.String) && _redirectNamespaces.Contains(tr.Namespace.String))
                    return true;
            }
            return false;
        }
    }
    
}

/*
    This is the "custom" "hooked" "stubbed" version of windows' notification library. 
    Pretty much everything below is AI generated. It really doesn't matter since it's all boilerplate
    that does nothing. If this ever causes problems, maybe rewrite properly.
 */

#pragma warning disable
namespace Windows.UI.Notifications
{
    public static class ToastNotificationManager {
        public static XmlDocument GetTemplateContent(ToastTemplateType type) {
            return new XmlDocument();
        }

        public static ToastNotifier CreateToastNotifier(string applicationId = null) {
            return new ToastNotifier();
        }
    }

    public enum ToastTemplateType {
        ToastImageAndText01,
        ToastImageAndText02,
        ToastImageAndText03,
        ToastImageAndText04,
        ToastText01,
        ToastText02,
        ToastText03,
        ToastText04
    }

    public class ToastNotifier {
        public void Show(ToastNotification toast) {
            toast.RaiseActivated();
        }
    }


    public class ToastNotification {
        public ToastNotification(object content) {
            Content = content;
        }

        public object Content { get; }
        public DateTimeOffset? ExpirationTime { get; set; }
        public bool ExpiresOnReboot { get; set; }

        public event EventHandler Activated;
        public event EventHandler<object> Dismissed;
        internal void RaiseActivated()
            => Activated?.Invoke(this, EventArgs.Empty);

        internal void RaiseDismissed(object reason = null)
            => Dismissed?.Invoke(this, reason);
    }

    public interface IXmlNode { }

    public class XmlDocument : IXmlNode {
        private readonly Dictionary<string, XmlNodeList> nodes = new Dictionary<string, XmlNodeList>();

        public XmlDocument() {
            nodes["text"] = new XmlNodeList { new XmlElement("text"), new XmlElement("text") };
            nodes["image"] = new XmlNodeList { new XmlElement("image") };
            nodes["toast"] = new XmlNodeList { new XmlElement("toast") };
        }

        public XmlNodeList GetElementsByTagName(string name)
            => nodes.TryGetValue(name, out var list) ? list : new XmlNodeList();

        public XmlText CreateTextNode(string value)
            => new XmlText(value);

        public IXmlNode SelectSingleNode(string xpath) {
            if (xpath == "/toast")
                return nodes["toast"][0];
            return null;
        }
    }

    public class XmlNodeList : List<XmlElement>, IXmlNode { }

    public class XmlElement : IXmlNode {
        public string Name { get; }
        private readonly Dictionary<string, string> attributes = new Dictionary<string, string>();

        public XmlElement(string name) => Name = name;

        public void SetAttribute(string name, string value)
            => attributes[name] = value;

        public void AppendChild(XmlText textNode) { }
    }

    public class XmlText : IXmlNode {
        public string Value { get; }
        public XmlText(string value) => Value = value;
    }
}
