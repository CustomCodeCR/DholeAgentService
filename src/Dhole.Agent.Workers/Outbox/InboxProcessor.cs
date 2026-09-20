using CustomCodeFramework.Messaging.Inbox;
using CustomCodeFramework.Messaging.Outbox.Processing;
using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Dhole.Agent.Workers.Outbox;

internal sealed class InboxProcessor(ServiceDbContext dbContext):IInboxProcessor
{
    public async Task<InboxProcessingResult> CleanupAsync(TimeSpan olderThan,int batchSize,CancellationToken cancellationToken=default)
    {
        var limit=DateTime.UtcNow.Subtract(olderThan);
        var messages=await dbContext.InboxMessages.Where(x=>x.Status==InboxMessageStatus.Processed&&x.ProcessedAtUtc!=null&&x.ProcessedAtUtc<limit).OrderBy(x=>x.ProcessedAtUtc).Take(batchSize).ToListAsync(cancellationToken);
        if(messages.Count==0)return InboxProcessingResult.Empty;
        dbContext.InboxMessages.RemoveRange(messages);await dbContext.SaveChangesAsync(cancellationToken);
        var more=await dbContext.InboxMessages.AnyAsync(x=>x.Status==InboxMessageStatus.Processed&&x.ProcessedAtUtc!=null&&x.ProcessedAtUtc<limit,cancellationToken);
        return new InboxProcessingResult(messages.Count,more);
    }
}
