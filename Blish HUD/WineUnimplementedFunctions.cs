using dnlib.DotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Windows.Data.Xml.Dom;
using Windows.Foundation;

namespace WineUnimplementedFunctions {
    /// <summary>
    /// Used to stub certain functions we don't care about that are unimplemented in WINE so they just do nothing instead of crashing.
    /// </summary>
    public static class WineBlishModuleLoader {

        private static readonly string[] _redirectNamespaces = new[]
    {
        "Windows.UI.Notifications",
        "Windows.Data.Xml.Dom",
        "Windows.Foundation",
        "Windows.Foundation.UniversalApiContract",
        "Windows.Foundation.FoundationContract",
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
    This is the "custom" "hooked" "stubbed" version of WinRT types. 
    Pretty much everything below is AI generated. It really doesn't matter since it's all boilerplate
    that does nothing. If this ever causes problems, maybe rewrite properly.
 */

#pragma warning disable
namespace Windows.UI.Notifications {
    public enum ToastDismissalReason {
        ApplicationHidden = 0,
        UserCanceled = 1,
        TimedOut = 2
    }

    public sealed class ToastDismissedEventArgs {
        public ToastDismissedEventArgs(ToastDismissalReason reason) => Reason = reason;
        public ToastDismissalReason Reason { get; }
    }

    public sealed class ToastFailedEventArgs {
        public ToastFailedEventArgs(int errorCode = 0) => ErrorCode = errorCode;
        public int ErrorCode { get; }
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

    public enum NotificationSetting {
        Enabled,
        DisabledForApplication,
        DisabledForUser,
        DisabledByGroupPolicy,
        DisabledByManifest
    }

    public static class ToastNotificationManager {
        public static XmlDocument GetTemplateContent(ToastTemplateType type) => new XmlDocument();

        public static ToastNotifier CreateToastNotifier(string applicationId = null) => new ToastNotifier();

        public static ToastNotifier CreateToastNotifier() => new ToastNotifier();

        public static ToastNotificationHistory History { get; } = new ToastNotificationHistory();
    }

    public sealed class ToastNotifier {
        public NotificationSetting Setting => NotificationSetting.Enabled;

        public void Show(ToastNotification toast) {
            toast.RaiseActivated();
            toast.RaiseDismissed(new ToastDismissedEventArgs(ToastDismissalReason.ApplicationHidden));
        }

        public void Hide(ToastNotification toast) { }

        public IList<ScheduledToastNotification> GetScheduledToastNotifications() => new List<ScheduledToastNotification>();

        public void AddToSchedule(ScheduledToastNotification scheduledToast) { }

        public void RemoveFromSchedule(ScheduledToastNotification scheduledToast) { }
    }

    public sealed class ToastNotification {
        public ToastNotification(object content) { Content = content; }

        public object Content { get; }
        public DateTimeOffset? ExpirationTime { get; set; }
        public bool ExpiresOnReboot { get; set; }
        public string Tag { get; set; }
        public string Group { get; set; }
        public bool SuppressPopup { get; set; }

        public event TypedEventHandler<ToastNotification, object> Activated;
        public event TypedEventHandler<ToastNotification, ToastDismissedEventArgs> Dismissed;
        public event TypedEventHandler<ToastNotification, ToastFailedEventArgs> Failed;
        internal void RaiseActivated() => Activated?.Invoke(this, null);
        internal void RaiseDismissed(ToastDismissedEventArgs args) => Dismissed?.Invoke(this, args);
        internal void RaiseFailed(int errorCode = 0) => Failed?.Invoke(this, new ToastFailedEventArgs(errorCode));
    }

    public sealed class ScheduledToastNotification {
        public ScheduledToastNotification(object content, DateTimeOffset deliveryTime) {
            Content = content;
            DeliveryTime = deliveryTime;
        }

        public object Content { get; }
        public DateTimeOffset DeliveryTime { get; }
        public string Tag { get; set; }
        public string Group { get; set; }
        public string Id { get; set; }
        public bool SuppressPopup { get; set; }
    }

    public sealed class ToastNotificationHistory {
        public void Clear() { }
        public void Remove(string tag) { }
        public void Remove(string tag, string group) { }
        public void RemoveGroup(string group) { }
    }
}

namespace Windows.Data.Xml.Dom {
    public class XmlDocument {
        public XmlNodeList GetElementsByTagName(string name) => new XmlNodeList();
        public IXmlNode SelectSingleNode(string xpath) => new XmlNode();
        public object CreateTextNode(string text) => text;
    }

    public class XmlNodeList : List<IXmlNode> { }

    public interface IXmlNode { }

    public class XmlNode : IXmlNode { }

    public class XmlElement : XmlNode {
        private readonly Dictionary<string, string> _attrs = new Dictionary<string, string>();

        public void SetAttribute(string name, string value) => _attrs[name] = value;
    }
}

namespace Windows.Foundation {
    public static class UniversalApiContract { }
    public static class FoundationContract { }

    public delegate void TypedEventHandler<TSender, TResult>(TSender sender, TResult args);
    public delegate void EventHandler<TSender, TResult>(TSender sender, TResult args);

    public struct HResult { public int Value; }
    public struct DateTime { }
    public struct TimeSpan { }
    public struct Point { }
    public struct Rect { }
    public struct Size { }

    public struct EventRegistrationToken {
        public ulong Value;
    }

    public interface IAsyncInfo { }
    public interface IClosable { void Close(); }
    public interface IAsyncAction : IAsyncInfo {
        void Completed(AsyncActionCompletedHandler handler);
    }

    public interface IAsyncOperation<TResult> : IAsyncInfo {
        void Completed(AsyncOperationCompletedHandler<TResult> handler);
    }

    public interface IAsyncOperationWithProgress<TResult, TProgress> : IAsyncInfo {
        void Progress(AsyncOperationProgressHandler<TResult, TProgress> handler);
        void Completed(AsyncOperationWithProgressCompletedHandler<TResult, TProgress> handler);
    }

    public interface IAsyncActionWithProgress<TProgress> : IAsyncInfo {
        void Progress(AsyncActionProgressHandler<TProgress> handler);
        void Completed(AsyncActionWithProgressCompletedHandler<TProgress> handler);
    }

    public delegate void AsyncActionCompletedHandler(IAsyncAction action, HResult asyncStatus);
    public delegate void AsyncActionProgressHandler<TProgress>(IAsyncAction action, TProgress progressInfo);
    public delegate void AsyncActionWithProgressCompletedHandler<TProgress>(IAsyncAction action, HResult asyncStatus);
    public delegate void AsyncOperationCompletedHandler<TResult>(IAsyncOperation<TResult> operation, TResult args);
    public delegate void AsyncOperationProgressHandler<TResult, TProgress>(IAsyncOperationWithProgress<TResult, TProgress> operation, TProgress progressInfo);
    public delegate void AsyncOperationWithProgressCompletedHandler<TResult, TProgress>(IAsyncOperationWithProgress<TResult, TProgress> operation, TResult args);

    public struct PropertyType { }
    public interface IPropertyValue { }
    public interface IReference<T> { }
    public interface IStringable {
        string ToString();
    }

    public class Deferral {
        public void Complete() { }
    }

    public class Uri {
        public Uri(string uri) { }
        public string Path => "";
    }

    public class MemoryBuffer { }
}

namespace Windows.Foundation.Metadata {
    public class ApiInformation {
        public static bool IsApiContractPresent(string contract, ushort version) => true;
    }

    public class WebHostHiddenAttribute : Attribute { }
    public class ActivatableAttribute : Attribute {
        public ActivatableAttribute() { }
        public ActivatableAttribute(int version) { }
    }
    public class ContractVersionAttribute : Attribute {
        public ContractVersionAttribute(Type contract, int version) { }
    }
}