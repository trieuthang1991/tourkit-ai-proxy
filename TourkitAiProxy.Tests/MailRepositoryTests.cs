using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Mail;
using Xunit;

namespace TourkitAiProxy.Tests;

public class MailRepositoryTests
{
    private static MailItem Item(string id, string category, string status, string subject = "S", string body = "B")
        => new(id, new MailContact("N", "n@x.com"), subject, body, "2026-06-05T08:30:00.0000000Z",
               false, category, status, null, null);

    private static MailRepository NewRepo(out FakeWebHostEnvironment env)
    {
        env = new FakeWebHostEnvironment();
        return new MailRepository(env, NullLogger<MailRepository>.Instance);
    }

    [Fact]
    public void Upsert_then_Get_roundtrips()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            repo.Upsert(Item("<a@x>", "hoi_dat_tour", "moi"));
            var got = repo.Get("<a@x>");
            Assert.NotNull(got);
            Assert.Equal("hoi_dat_tour", got!.Category);
        }
    }

    [Fact]
    public void Has_true_after_upsert()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            Assert.False(repo.Has("<x>"));
            repo.Upsert(Item("<x>", "spam", "moi"));
            Assert.True(repo.Has("<x>"));
        }
    }

    [Fact]
    public void Filter_by_status_and_category_and_search()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            repo.Upsert(Item("<1>", "hoi_dat_tour", "moi", subject: "Đặt tour Phú Quốc"));
            repo.Upsert(Item("<2>", "khieu_nai", "da_dong", subject: "Phàn nàn"));
            repo.Upsert(Item("<3>", "hoi_dat_tour", "da_dong", subject: "Hỏi Đà Nẵng"));

            Assert.Equal(2, repo.Filter(status: null, category: "hoi_dat_tour", search: null).Count);
            Assert.Equal(2, repo.Filter(status: "da_dong", category: null, search: null).Count);
            Assert.Single(repo.Filter(status: null, category: null, search: "phu quoc"));   // bỏ dấu vẫn khớp 'Phú Quốc'
        }
    }

    [Fact]
    public void Counts_groups_by_status_and_category()
    {
        var repo = NewRepo(out var env);
        using (env)
        {
            repo.Upsert(Item("<1>", "hoi_dat_tour", "moi"));
            repo.Upsert(Item("<2>", "hoi_dat_tour", "moi"));
            repo.Upsert(Item("<3>", "spam", "da_dong"));

            var counts = repo.Counts();
            Assert.Equal(3, counts.Total);
            Assert.Equal(2, counts.ByStatus["moi"]);
            Assert.Equal(2, counts.ByCategory["hoi_dat_tour"]);
        }
    }

    [Fact]
    public void Persists_across_instances()
    {
        var env = new FakeWebHostEnvironment();
        using (env)
        {
            var repo1 = new MailRepository(env, NullLogger<MailRepository>.Instance);
            repo1.Upsert(Item("<keep>", "xac_nhan", "moi"));

            var repo2 = new MailRepository(env, NullLogger<MailRepository>.Instance);
            Assert.NotNull(repo2.Get("<keep>"));
        }
    }
}
