// The design's pastel palette for avatars. A person always gets the same color.
const colors = ['#c5d8ff', '#dfcaff', '#bdebd7', '#ffd9b8', '#ffc9d6', '#c9f0ff']

export type Person = {name: string; initials?: string; color?: string}

function initialsOf(name: string): string {
    const parts = name.trim().split(/[\s._-]+/).filter(Boolean)
    if (parts.length === 0) return '?'
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
}

function colorOf(name: string): string {
    let hash = 0
    for (const char of name) hash = (hash * 31 + char.charCodeAt(0)) | 0
    return colors[Math.abs(hash) % colors.length]
}

export function Avatar({person}: {person: Person}) {
    return (
        <span className="avatar" style={{background: person.color ?? colorOf(person.name)}} title={person.name}>
            {person.initials ?? initialsOf(person.name)}
        </span>
    )
}
