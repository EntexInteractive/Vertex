import {Avatar} from '../../components/Avatar'
import {Badge} from '../../components/DataTable'
import {ViewFrame} from '../../components/ViewFrame'
import {formatRevision} from '../../lib/format'
import {graphRevisions} from '../../mocks/sampleData'

export function GraphPage() {
    return (
        <ViewFrame
            eyebrow="EXPERIMENTAL"
            title="Branch graph"
            description="How branches relate and where revisions were merged."
            action={{label: 'Refresh graph', icon: 'refresh', disabled: true}}
            preview
        >
            <div className="graph-shell">
                <div className="graph-canvas">
                    <svg viewBox="0 0 230 440" aria-hidden="true">
                        <path d="M48 35v74c0 35 54 32 54 67v132c0 35 64 29 64 67v50" className="line-purple"/>
                        <path d="M102 176v132" className="line-green"/>
                        <path d="M166 242c0 35-64 33-64 66" className="line-orange"/>
                        <path d="M166 375v50" className="line-blue"/>
                    </svg>
                    {graphRevisions.map((revision, i) => (
                        <i key={revision.number} className={`node n${i}`}/>
                    ))}
                </div>
                <div className="graph-list">
                    {graphRevisions.map(revision => (
                        <div className="graph-item" key={revision.number}>
                            <div>
                                <b>{revision.message}</b>
                                <span>
                                    <Badge text={revision.branch}/>
                                    <code>{formatRevision(revision.number)}</code>
                                </span>
                            </div>
                            <span>
                                <Avatar person={revision.author}/>
                                {revision.author.name}
                            </span>
                            <small>{revision.when}</small>
                        </div>
                    ))}
                </div>
            </div>
        </ViewFrame>
    )
}
