// pages/quotes.jsx — ví dụ trang thứ 2: list tất cả tour đã cache trong localStorage.
// Demo pattern thêm page: 1 file + 1 <Route> + 1 <Link>. Không cần build, không cần router lib.
//
// Truy cập: /#/quotes (hoặc bấm Link ở header)

function QuotesPage({ pushToast }) {
  const [items, setItems] = React.useState(() => loadCachedTours());
  const Storage = window.tourkitStorage;
  const { navigate } = window.tourkitRouter;

  function loadCachedTours() {
    const list = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (!key || !key.startsWith('tourkit_tour_v')) continue;
      try {
        const v = JSON.parse(localStorage.getItem(key));
        if (v && v.ts) list.push({ key, ts: v.ts, marketing: v.marketing, itinerary: v.itinerary });
      } catch {}
    }
    list.sort((a, b) => b.ts - a.ts);
    return list;
  }

  const refresh = () => setItems(loadCachedTours());

  const remove = (key) => {
    localStorage.removeItem(key);
    refresh();
    pushToast('Đã xoá tour khỏi cache');
  };

  return (
    <main className="page" style={{padding: '24px 32px'}}>
      <div style={{display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 20}}>
        <div>
          <h2 style={{margin: 0, fontSize: 22, fontWeight: 700}}>Tour đã tạo</h2>
          <div style={{color: 'var(--text-3)', fontSize: 13, marginTop: 2}}>{items.length} tour trong cache local (TTL 7 ngày)</div>
        </div>
        <button className="btn btn-ghost btn-sm" onClick={refresh}>
          <Icon name="refresh" size={14} /> Refresh
        </button>
      </div>

      {items.length === 0 ? (
        <div style={{padding: 60, textAlign: 'center', color: 'var(--text-3)', background: '#fafafa', borderRadius: 12}}>
          <div style={{fontSize: 14, marginBottom: 8}}>Chưa có tour nào trong cache.</div>
          <button className="btn btn-primary btn-sm" onClick={() => navigate('/')}>Tạo tour mới</button>
        </div>
      ) : (
        <div style={{display: 'grid', gap: 12}}>
          {items.map(item => {
            const ageHr = Math.round((Date.now() - item.ts) / 3600e3);
            const ageStr = ageHr < 24 ? `${ageHr}h trước` : `${Math.floor(ageHr / 24)} ngày trước`;
            return (
              <div key={item.key} style={{
                display: 'flex', justifyContent: 'space-between', alignItems: 'center',
                padding: '14px 18px', background: 'white', border: '1px solid #e5e7eb',
                borderRadius: 10
              }}>
                <div style={{flex: 1, minWidth: 0}}>
                  <div style={{fontSize: 15, fontWeight: 600, marginBottom: 2}}>
                    {item.marketing?.tourName || 'Tour không tên'}
                  </div>
                  <div style={{fontSize: 12, color: 'var(--text-3)'}}>
                    {item.itinerary?.length || 0} ngày · {ageStr}
                  </div>
                </div>
                <button className="btn btn-ghost btn-sm" onClick={() => remove(item.key)} title="Xoá khỏi cache">×</button>
              </div>
            );
          })}
        </div>
      )}
    </main>
  );
}

window.QuotesPage = QuotesPage;
