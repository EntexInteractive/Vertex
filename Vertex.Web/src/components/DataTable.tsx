// Building blocks for the table-style pages (History, Tasks, Bugs).

export function TableHead({cols}: {cols: string[]}) {
    return (
        <div className="table-head" role="row">
            {cols.map(col => (
                <span key={col} role="columnheader">{col}</span>
            ))}
        </div>
    )
}

export function Summary({value, label, warning = false}: {value: string; label: string; warning?: boolean}) {
    return (
        <div className={`summary ${warning ? 'warning' : ''}`}>
            <strong>{value}</strong>
            <span>{label}</span>
        </div>
    )
}

/** A colored pill. The class is derived from the text, e.g. "In progress" → `badge in-progress`. */
export function Badge({text}: {text: string}) {
    const slug = text.toLowerCase().replace(/[ /]/g, '-')
    return <span className={`badge ${slug}`}>{text}</span>
}
