(() => {
    const main = document.querySelector('main main') || document.querySelector('main');
    if (!main) return JSON.stringify({error: 'no main'});
    const links = main.querySelectorAll('a[href*="/in/"]');
    const info = [];
    for (let i = 0; i < Math.min(links.length, 5); i++) {
        const link = links[i];
        const container = link.closest('li');
        const paragraphs = link.querySelectorAll('p');
        const pTexts = [];
        for (const p of paragraphs) pTexts.push(p.textContent.trim());
        info.push({
            href: link.getAttribute('href'),
            linkClasses: link.className.substring(0, 150),
            pTexts: pTexts,
            containerClasses: container ? container.className.substring(0, 150) : 'none',
            connectBtn: container ? (container.querySelector('button') ? container.querySelector('button').textContent.trim() : 'none') : 'none'
        });
    }
    return JSON.stringify(info, null, 2);
})()
