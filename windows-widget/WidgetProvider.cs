using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Windows.Widgets.Providers;

namespace CodexUsageMeter.WindowsWidget;

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IWidgetProvider)), Guid(ClassId)]
public sealed class WidgetProvider : IWidgetProvider
{
    public const string ClassId="6BA7A0D2-8E9C-4F5D-A263-624316ECC101";
    public const string DefinitionId="CodexUsageSummary";
    private sealed class Instance
    {
        internal string Size="Medium";
        internal bool Active;
        internal string LastTemplate="";
        internal string? Notice;
        internal int ActionSequence;
    }
    private readonly object gate=new();
    private readonly Dictionary<string,Instance> instances=new();
    private readonly Timer refreshTimer;

    public WidgetProvider()
    {
        try
        {
            foreach(var info in WidgetManager.GetDefault().GetWidgetInfos())
                if(info.WidgetContext.DefinitionId==DefinitionId)
                    instances[info.WidgetContext.Id]=new Instance{Size=info.WidgetContext.Size.ToString()};
        }
        catch(Exception error){WidgetFiles.Log("recover",error);}
        refreshTimer=new Timer(_=>RefreshActive(),null,TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(15));
    }
    public void CreateWidget(WidgetContext context)=>Accept(context,true);
    public void Activate(WidgetContext context)=>Accept(context,true);
    public void OnWidgetContextChanged(WidgetContextChangedArgs args)=>Accept(args.WidgetContext,true);
    public void DeleteWidget(string widgetId,string customState)
    {
        lock(gate)
        {
            instances.Remove(widgetId);
            if(instances.Count==0){refreshTimer.Dispose();Program.Shutdown.Set();}
        }
    }
    public void Deactivate(string widgetId){lock(gate)if(instances.TryGetValue(widgetId,out var value))value.Active=false;}
    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        string id=args.WidgetContext.Id;
        if(args.Verb is not "open" and not "refresh")return;
        string verb=args.Verb;
        int sequence;
        lock(gate)
        {
            if(!instances.TryGetValue(id,out var instance))instances[id]=instance=new Instance{Size=args.WidgetContext.Size.ToString(),Active=true};
            sequence=++instance.ActionSequence;
        }
        // Observe IPC failure (including the meter's six-second timeout) without blocking the COM callback.
        _=Task.Run(()=>
        {
            string? error=WidgetFiles.Invoke(verb);
            lock(gate)
                if(instances.TryGetValue(id,out var instance) && instance.ActionSequence==sequence)
                {instance.Notice=error;Update(id,instance,true);}
        });
    }
    private void Accept(WidgetContext context,bool active)
    {
        // COM callback objects are valid only inside the callback. Retain plain values only.
        if(context.DefinitionId!=DefinitionId)return;
        lock(gate)
        {
            if(!instances.TryGetValue(context.Id,out var value))instances[context.Id]=value=new Instance();
            value.Size=context.Size.ToString();value.Active=active;value.Notice=null;
            Update(context.Id,value,true);
        }
    }
    private void RefreshActive()
    {
        lock(gate)
            foreach(var pair in instances.Where(x=>x.Value.Active))Update(pair.Key,pair.Value,false);
    }
    private void Update(string id,Instance instance,bool force)
    {
        try
        {
            var card=WidgetContent.Build(WidgetFiles.ReadSnapshot(),instance.Size,DateTimeOffset.UtcNow);
            if(instance.Notice is not null)
            {
                // Error guidance replaces rows so it remains visible even in the smallest host surface.
                card["body"]!.AsArray().Clear();
                card["body"]!.AsArray().Add(new JsonObject{["type"]="TextBlock",["text"]=instance.Notice,["size"]="Small",["wrap"]=true,["maxLines"]=2,["color"]="Attention"});
            }
            string template=card.ToJsonString();
            if(!force && template==instance.LastTemplate)return;
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id){Template=template,Data="{}",CustomState="v1"});
            instance.LastTemplate=template;
        }
        catch(Exception error){WidgetFiles.Log("update",error);}
    }
}
