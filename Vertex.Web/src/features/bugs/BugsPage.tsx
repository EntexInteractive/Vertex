import {Badge, Summary, TableHead} from '../../components/DataTable'
import {Icon} from '../../components/Icon'
import {ViewFrame} from '../../components/ViewFrame'
import {bugs} from '../../mocks/sampleData'

export function BugsPage() {
    return (
        <ViewFrame
            eyebrow="QUALITY"
            title="Bug tracker"
            description="Defects reported against builds of this project."
            action={{label: 'Report bug', icon: 'bug', disabled: true}}
            preview
        >
            <div className="summary-row">
                <Summary value="1" label="High priority" warning/>
                <Summary value="2" label="Medium priority"/>
                <Summary value="1" label="Low priority"/>
                <Summary value="94" label="Health score"/>
            </div>
            <TableHead cols={['ID', 'ISSUE', 'SEVERITY', 'STATUS', 'UPDATED']}/>
            <div className="data-table">
                {bugs.map(bug => (
                    <div className="data-row bug-data" key={bug.id}>
                        <code>{bug.id}</code>
                        <span>{bug.title}</span>
                        <Badge text={bug.severity}/>
                        <Badge text={bug.status}/>
                        <small>
                            {bug.updated}
                            <Icon name="chevron" size={13}/>
                        </small>
                    </div>
                ))}
            </div>
        </ViewFrame>
    )
}
